using System.Buffers.Binary;
namespace PlainViewer.Core;

public static partial class LegacySpreadsheets
{
    // Pictures and charts in Excel 97-2003 workbooks. Pictures are kept once for the whole workbook in the drawing group
    // (MSODRAWINGGROUP: an Office Art picture store); each sheet's drawing (MSODRAWING) lists its shapes with their cell
    // anchors, and each shape is followed by an OBJ record saying what it is. A chart is a chart substream after its
    // OBJ record. Its series come from the chart's own saved values when the file has them (the SIIndex cache Excel
    // writes), otherwise from the saved values of the cells the series refer to; nothing is recalculated. Pictures are
    // checked and written to the work folder like .xlsx pictures (SheetDrawings); linked pictures are never opened.
    private sealed partial class Excel97
    {
        private byte[] drawingGroup = [];
        private readonly List<bool> supBooks = [];                                 // per SUPBOOK: whether it is this workbook
        private readonly List<(int SupBook, int Tab)> externSheets = [];            // EXTERNSHEET entries, for 3-D references
        private List<(byte[]? Bytes, bool Unsupported)> blips = [];
        private readonly SheetDrawings.Budget pictureBudget = new();
        private readonly Dictionary<int, ChartSpec> charts = [];                   // by the offset of the chart's BOF record
        private readonly Dictionary<int, List<Range>> chartRanges = [];            // per sheet tab: cells charts refer to
        private readonly Dictionary<(int Tab, int Row, int Column), (double? Value, string Text)> chartCells = [];
        private readonly List<(SheetPicture Placed, ChartSpec Spec)> pendingCharts = [];

        private readonly record struct Range(int Tab, int Row1, int Row2, int Column1, int Column2);

        private sealed class SeriesSpec
        {
            public string? Name, Colour, LineColour;
            public Range? NameCell, Values, Categories;
            public int Group, Labels;                                              // Labels: AttachedLabel flags
            public bool Child;                                                     // a trendline or error bars, not a series
            public readonly SortedDictionary<int, double> Cached = [];
        }

        // The text of a text box or shape (TXO with its CONTINUE records): characters, runs (first character, font), flags.
        private sealed record TextObject(string Text, List<(int Char, int Font)> Runs, int Flags);

        private sealed class ChartSpec
        {
            public string Type = "", Title = "", CategoryTitle = "", ValueTitle = "";
            public bool Stacked, Percent, Unsupported;
            public int Groups;
            public readonly List<SeriesSpec> Series = [];
            public readonly SortedDictionary<int, string> CachedCategories = [];
        }

        private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
        private static string Hex(byte[] d, int at) => $"#{d[at]:x2}{d[at + 1]:x2}{d[at + 2]:x2}";

        // Every chart substream in a sheet (or the chart sheet itself), with the cells its series refer to.
        private void FindCharts(int offset)
        {
            var records = Records(offset);
            for (int i = 0; i < records.Count; i++)
            {
                var (type, at, length) = records[i];
                if (type != 0x0809 || length < 4) continue;
                int end = SubstreamEnd(records, i);
                if (U16(data, at + 2) == 0x0020 && charts.Count < SheetDrawings.MaxPictures * 4)
                {
                    var spec = charts[at] = ParseChart(records, i, end);
                    foreach (var series in spec.Series)
                        foreach (var range in new[] { series.NameCell, series.Values, series.Categories })
                            if (range is { } r)
                            {
                                if (!chartRanges.TryGetValue(r.Tab, out var list)) chartRanges[r.Tab] = list = [];
                                if (list.Count < 1000) list.Add(r);
                            }
                }
                if (i > 0) i = end;
            }
        }

        private ChartSpec ParseChart(List<Record> records, int start, int end)
        {
            var spec = new ChartSpec();
            var blocks = new List<int>();                  // the record type that opened each Begin ... End block
            int last = 0, group = -1, cache = 0;
            SeriesSpec? series = null;
            string? text = null;
            bool seriesFormat = false;
            for (int k = start + 1; k < end; k++)
            {
                var (type, at, length) = records[k];
                bool inText = blocks.Contains(0x1025), inSeries = series is not null && blocks.Contains(0x1003);
                bool formatting = inSeries && seriesFormat && blocks.Count > 0 && blocks[^1] == 0x1006;
                switch (type)
                {
                    case 0x1033: blocks.Add(last); break;                                           // Begin
                    case 0x1034 when blocks.Count > 0:                                              // End
                        int closed = blocks[^1]; blocks.RemoveAt(blocks.Count - 1);
                        if (closed == 0x1003) series = null;
                        if (closed == 0x1025) text = null;
                        break;
                    case 0x1003 when spec.Series.Count < SheetDrawings.MaxSeries: series = new SeriesSpec(); spec.Series.Add(series); break;
                    case 0x1003: series = null; break;
                    case 0x104A when series is not null: series.Child = true; break;               // SerParent
                    case 0x1045 when series is not null && length >= 2: series.Group = U16(data, at); break;   // SerToCrt
                    case 0x1051 when length >= 8 && inSeries && !inText:                              // BRAI
                        if (data[at + 1] == 2 && Reference(at + 8, Math.Min(U16(data, at + 6), length - 8)) is { } range)
                            switch (data[at])
                            {
                                case 0: series!.NameCell = range with { Row2 = range.Row1, Column2 = range.Column1 }; break;
                                case 1: series!.Values = range; break;
                                case 2: series!.Categories = range; break;
                            }
                        break;
                    case 0x100D when length >= 4:                                                   // SeriesText
                        string value = XlString(at + 2, at + length, twoByteCount: false, out _);
                        if (inText) text = value; else if (inSeries && blocks[^1] == 0x1003) series!.Name = value;
                        break;
                    case 0x1027 when length >= 2 && inText && text is not null:                     // ObjectLink: what the text belongs to
                        switch (U16(data, at)) { case 1: spec.Title = text; break; case 2: spec.ValueTitle = text; break; case 3: spec.CategoryTitle = text; break; }
                        break;
                    case 0x100C when formatting && length >= 2: series!.Labels = U16(data, at); break;   // AttachedLabel: the series' data labels
                    case 0x1006 when length >= 2: seriesFormat = U16(data, at) == 0xFFFF; break;   // DataFormat for the whole series
                    case 0x100A when formatting && length >= 12 && (U16(data, at + 10) & 1) == 0 && U16(data, at + 8) != 0: series!.Colour = Hex(data, at); break;
                    case 0x1007 when formatting && length >= 10 && (U16(data, at + 8) & 1) == 0 && U16(data, at + 4) != 5: series!.LineColour = Hex(data, at); break;
                    case 0x1014: group++; spec.Groups++; break;                                     // ChartFormat: a chart group
                    case 0x1017 when group == 0 && length >= 6:
                        { int f = U16(data, at + 4); spec.Type = (f & 1) != 0 ? "bar" : "column"; spec.Stacked = (f & 2) != 0; spec.Percent = (f & 4) != 0; }
                        break;
                    case 0x1018 or 0x101A when group == 0 && length >= 2:
                        { int f = U16(data, at); spec.Type = type == 0x1018 ? "line" : "area"; spec.Stacked = (f & 1) != 0; spec.Percent = (f & 2) != 0; }
                        break;
                    case 0x1019 when group == 0 && length >= 4: spec.Type = U16(data, at + 2) > 0 ? "doughnut" : "pie"; break;
                    case 0x101B when group == 0: spec.Type = "scatter"; break;
                    case 0x103E or 0x1040 or 0x103F or 0x1066 when group == 0: spec.Unsupported = true; break;   // radar, surface, bar of pie
                    case 0x1065 when length >= 2: cache = U16(data, at); break;                   // SIIndex: what the next cells hold
                    case 0x0203 when length >= 14 && cache is 1 or 2:
                        {
                            int point = U16(data, at), index = U16(data, at + 2);
                            double v = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at + 6));
                            if (point >= SheetDrawings.MaxPoints || !double.IsFinite(v)) break;
                            if (cache == 1 && index < spec.Series.Count) spec.Series[index].Cached[point] = v;
                            else if (cache == 2) spec.CachedCategories[point] = Number(v, U16(data, at + 4));
                        }
                        break;
                    case 0x0204 when length >= 9 && cache == 2 && U16(data, at) < SheetDrawings.MaxPoints:
                        spec.CachedCategories[U16(data, at)] = XlString(at + 6, at + length, twoByteCount: true, out _);
                        break;
                }
                last = type;
            }
            return spec;
        }

        // A 3-D cell or area reference (the chart's formula for a series part) within this workbook.
        private Range? Reference(int at, int size)
        {
            if (size < 7) return null;
            byte ptg = data[at];
            int ixti = U16(data, at + 1);
            if (ixti >= externSheets.Count || externSheets[ixti].SupBook >= supBooks.Count || !supBooks[externSheets[ixti].SupBook]) return null;
            int tab = externSheets[ixti].Tab;
            if (ptg is 0x3A or 0x5A or 0x7A)
            {
                int row = U16(data, at + 3), column = U16(data, at + 5) & 0x3FFF;
                return new Range(tab, row, row, column, column);
            }
            if (ptg is 0x3B or 0x5B or 0x7B && size >= 11)
            {
                int r1 = U16(data, at + 3), r2 = U16(data, at + 5), c1 = U16(data, at + 7) & 0x3FFF, c2 = U16(data, at + 9) & 0x3FFF;
                if (r2 < r1 || c2 < c1) return null;
                r2 = Math.Min(r2, r1 + SheetDrawings.MaxPoints - 1); c2 = Math.Min(c2, c1 + SheetDrawings.MaxPoints / (r2 - r1 + 1));
                return new Range(tab, r1, r2, c1, c2);
            }
            return null;
        }

        // The saved cells of a range, row by row.
        private IEnumerable<(double? Value, string Text)> Cells(Range range)
        {
            for (int r = range.Row1; r <= range.Row2; r++)
                for (int c = range.Column1; c <= range.Column2; c++)
                    yield return chartCells.TryGetValue((range.Tab, r, c), out var cell) ? cell : (null, "");
        }

        // Once every sheet has been read: each chart's data from its cache or from the cells it refers to.
        private void ResolveCharts()
        {
            foreach (var (placed, spec) in pendingCharts)
            {
                var chart = placed.Chart = new ChartData { Title = spec.Title, CategoryTitle = spec.CategoryTitle, ValueTitle = spec.ValueTitle,
                    Stacked = spec.Stacked, Percent = spec.Percent, Type = spec.Type.Length > 0 ? spec.Type : "column" };
                var labelled = new List<(ChartSeries Item, int Flags)>();
                if (spec.Unsupported || spec.Type.Length == 0) { chart.Notice = "This kind of chart is not shown in this version."; continue; }
                if (spec.Groups > 1) chart.Notice = "Only the first part of this combined chart is shown.";
                int index = 0;
                foreach (var s in spec.Series)
                {
                    index++;
                    if (s.Child || s.Group != 0) continue;
                    string? name = s.Name ?? (s.NameCell is { } n ? Cells(n).First().Text : null);
                    var item = new ChartSeries
                    {
                        Name = name is { Length: > 0 } ? name : $"Series {index}",
                        Color = chart.Type is "line" or "scatter" ? s.LineColour ?? s.Colour : s.Colour ?? s.LineColour
                    };
                    if (s.Cached.Count > 0)
                        foreach (var (point, v) in s.Cached) { while (item.Values.Count < point) item.Values.Add(null); item.Values.Add(v); }
                    else if (s.Values is { } values) item.Values = Cells(values).Select(c => c.Value).ToList();
                    if (chart.Type == "scatter" && s.Categories is { } xs) item.X = Cells(xs).Select(c => c.Value).ToList();
                    if (chart.Categories.Count == 0)
                    {
                        if (spec.CachedCategories.Count > 0)
                            foreach (var (point, label) in spec.CachedCategories) { while (chart.Categories.Count < point) chart.Categories.Add(""); chart.Categories.Add(label); }
                        else if (s.Categories is { } categories) chart.Categories = Cells(categories).Select(c => c.Text).ToList();
                    }
                    chart.Series.Add(item);
                    if (s.Labels != 0) labelled.Add((item, s.Labels));
                }
                int points = chart.Series.Count == 0 ? 0 : chart.Series.Max(x => x.Values.Count);
                if (points == 0) { chart.Notice = "This chart has no data to show."; continue; }
                while (chart.Categories.Count < points) chart.Categories.Add((chart.Categories.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                // AttachedLabel flags: 1 value, 2 percentage, 4 category and percentage, 16 category.
                foreach (var (item, flags) in labelled)
                    item.PointLabels = SheetDrawings.PointLabels(chart, item, (flags & 1) != 0, (flags & 6) != 0, (flags & 0x14) != 0, null);
                if (placed.Description.Length == 0) placed.Description = chart.Title;
            }
        }

        private SheetData ReadChartSheet(string name, int offset)
        {
            var records = Records(offset);
            if (records.Count == 0 || records[0].Type != 0x0809) throw Damaged();
            var sheet = new SheetData { Name = name, ChartSheet = true };
            if (charts.TryGetValue(records[0].Offset, out var spec))
            {
                var placed = new SheetPicture();
                sheet.Pictures.Add(placed);
                pendingCharts.Add((placed, spec));
            }
            else sheet.Notice = "This chart sheet has no chart to show.";
            return sheet;
        }

        // ---- Office Art shapes ----

        private sealed class Shape
        {
            public int[]? Anchor;                          // column, dx (1/1024 of the column), row, dy (1/256 of the row), then the end
            public int Picture;                            // 1-based index into the picture store
            public bool HasObject, Linked;
            public int Type, Flags;                        // FSP: shape type, and flags (1 group, 4 patriarch, 0x40/0x80 flips)
            public readonly Dictionary<int, uint> Properties = [];
            public int[]? Child, GroupBox;                 // a group member's box in its group's coordinates; a group's coordinates
            public Shape? Parent;                          // the group the shape belongs to
            public bool Patriarch => (Flags & 0x4) != 0;
        }

        // The group a container's shapes belong to: its first shape is the group itself.
        private sealed class GroupScope { public Shape? Group, Parent; }

        // Walks Office Art records (containers are entered); `onAtom` sees every atom with the shape it belongs to.
        private static void Walk(byte[] d, int start, int end, int depth, Shape? shape, List<Shape> shapes, Action<int, int, int, int, Shape?> onAtom, GroupScope? scope = null)
        {
            for (int pos = start; pos + 8 <= end && depth < 16;)
            {
                int verInstance = U16(d, pos), type = U16(d, pos + 2);
                long length = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
                int body = pos + 8, stop = (int)Math.Min(end, body + length);
                if ((verInstance & 0xF) == 0xF)
                {
                    var inner = shape; var innerScope = scope;
                    if (type == 0xF003) innerScope = new GroupScope { Parent = scope?.Group is { Patriarch: false } enclosing ? enclosing : null };   // a group
                    if (type == 0xF004)                                                   // one shape
                    {
                        inner = new Shape(); shapes.Add(inner);
                        if (scope is not null)
                        {
                            if (scope.Group is null) { inner.Parent = scope.Parent; scope.Group = inner; }
                            else inner.Parent = scope.Group.Patriarch ? null : scope.Group;
                        }
                    }
                    Walk(d, body, stop, depth + 1, inner, shapes, onAtom, innerScope);
                }
                else onAtom(type, verInstance >> 4, body, stop, shape);
                if (stop <= pos) break;
                pos = stop;
            }
        }

        // The sheet's shapes paired with its OBJ records (each shape with client data has one, in the same order).
        private List<SheetPicture> Drawings(SheetData sheet, int index, byte[] drawing, List<(int Kind, int Chart)> objects, Dictionary<int, TextObject> texts, string? folder)
        {
            var result = new List<SheetPicture>();
            if (drawing.Length == 0 || objects.Count == 0) return result;
            var shapes = new List<Shape>();
            Walk(drawing, 0, drawing.Length, 0, null, shapes, (type, instance, body, stop, shape) =>
            {
                if (shape is null) return;
                switch (type)
                {
                    case 0xF00B or 0xF121 or 0xF122:                                   // properties
                        for (int k = 0; k < instance && body + k * 6 + 6 <= stop; k++)
                        {
                            int raw = U16(drawing, body + k * 6), id = raw & 0x3FFF;
                            uint value = BinaryPrimitives.ReadUInt32LittleEndian(drawing.AsSpan(body + k * 6 + 2));
                            if (id == 0x0104) shape.Picture = (int)Math.Min(value, int.MaxValue);
                            else if (id == 0x0105) shape.Linked = true;              // a file name the picture is linked to
                            else if ((raw & 0x8000) == 0 && shape.Properties.Count < 200) shape.Properties[id] = value;
                        }
                        break;
                    case 0xF00A when stop - body >= 8: shape.Type = instance; shape.Flags = (int)BinaryPrimitives.ReadUInt32LittleEndian(drawing.AsSpan(body + 4)); break;   // FSP
                    case 0xF009 when stop - body >= 16: shape.GroupBox = Box(drawing, body); break;   // FSPGR: the group's coordinates
                    case 0xF00F when stop - body >= 16: shape.Child = Box(drawing, body); break;      // child anchor
                    case 0xF010 when stop - body >= 18:                                // client anchor: the cells it spans
                        shape.Anchor = Enumerable.Range(0, 8).Select(k => U16(drawing, body + 2 + k * 2)).ToArray();
                        break;
                    case 0xF011: shape.HasObject = true; break;                        // client data: an OBJ record follows
                }
            });
            var placedShapes = shapes.Where(s => s.HasObject).ToList();
            for (int i = 0; i < placedShapes.Count && i < objects.Count && result.Count < SheetDrawings.MaxPictures; i++)
            {
                var shape = placedShapes[i];
                var (kind, chartAt) = objects[i];
                var (anchor, part) = Locate(shape, 0);
                if (anchor is not { } a) continue;
                var placed = new SheetPicture
                {
                    Part = part,
                    Column = a[0], ColumnOffset = Math.Min(a[1], 1024) / 1024.0 * SheetDrawings.ColumnPixels(sheet, a[0]),
                    Row = a[2], RowOffset = Math.Min(a[3], 256) / 256.0 * SheetDrawings.RowPixels(sheet, a[2]),
                    ToColumn = a[4], ToColumnOffset = Math.Min(a[5], 1024) / 1024.0 * SheetDrawings.ColumnPixels(sheet, a[4]),
                    ToRow = a[6], ToRowOffset = Math.Min(a[7], 256) / 256.0 * SheetDrawings.RowPixels(sheet, a[6])
                };
                if (kind == 5 && chartAt >= 0 && charts.TryGetValue(chartAt, out var spec)) { result.Add(placed); pendingCharts.Add((placed, spec)); }
                else if (kind == 8)
                {
                    if (shape.Picture >= 1 && shape.Picture <= blips.Count && blips[shape.Picture - 1] is var (bytes, unsupported))
                    {
                        if (bytes is not null) SheetDrawings.AddPicture(bytes, placed, folder, index, pictureBudget, result);
                        else if (unsupported) pictureBudget.Unsupported++;
                        else if (shape.Linked) pictureBudget.Linked++;
                    }
                    else if (shape.Linked) pictureBudget.Linked++;
                }
                // Lines, rectangles, ovals, arcs, text boxes, polygons and Office Art shapes (not groups, charts or pictures).
                else if (kind is 1 or 2 or 3 or 4 or 6 or 9 or 30)
                {
                    placed.Shape = ShapeOf(shape, texts.GetValueOrDefault(i));
                    placed.Description = string.Join(" ", placed.Shape.Paragraphs.Select(p => p.Text)).Trim();
                    result.Add(placed);
                }
            }
            return result;
        }

        private static int[] Box(byte[] d, int at) => [.. Enumerable.Range(0, 4).Select(k => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + k * 4)))];

        // The shape's top-level anchor and, for a group member, its box within it as fractions (through every group).
        private static (int[]? Anchor, double[]? Part) Locate(Shape shape, int depth)
        {
            if (shape.Parent is not { } group || depth > 8) return (shape.Anchor, null);
            var (anchor, part) = Locate(group, depth + 1);
            if (shape.Child is not { } c || group.GroupBox is not { } g || g[2] == g[0] || g[3] == g[1]) return (anchor, part);
            part ??= [0, 0, 1, 1];
            double width = g[2] - g[0], height = g[3] - g[1];
            return (anchor, [part[0] + (c[0] - g[0]) / width * part[2], part[1] + (c[1] - g[1]) / height * part[3], (c[2] - c[0]) / width * part[2], (c[3] - c[1]) / height * part[3]]);
        }

        // Office Art shape types by their outline (others, and freeform outlines, are drawn as rectangles).
        private static string Geometry(int type) => type switch
        {
            2 => "roundRect", 3 => "ellipse", 4 => "diamond", 5 => "triangle", 6 => "rtTriangle", 7 => "parallelogram", 9 => "hexagon",
            13 => "rightArrow", 66 => "leftArrow", 67 => "downArrow", 68 => "upArrow", 20 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 => "line", _ => "rect"
        };

        // A shape's look from its Office Art properties, and its text from the TXO record with the workbook's fonts.
        private ShapeData ShapeOf(Shape shape, TextObject? text)
        {
            uint Property(int id, uint missing) => shape.Properties.TryGetValue(id, out uint v) ? v : missing;
            // A boolean property set: the value's bit when its "use" bit (16 higher) is set, otherwise the default.
            bool Switch(int id, int bit, bool missing) =>
                shape.Properties.TryGetValue(id, out uint v) && (v & (1u << (bit + 16))) != 0 ? (v & (1u << bit)) != 0 : missing;
            // Office Art colours: 0x00bbggrr, or a palette entry (flag 0x08 in the high byte); system colours are not used.
            string? ArtColour(uint value) => (value >> 24) switch
            {
                0 => $"#{value & 0xFF:x2}{(value >> 8) & 0xFF:x2}{(value >> 16) & 0xFF:x2}",
                var flags when (flags & 0x08) != 0 => Colour((int)(value & 0xFF)),
                _ => null
            };
            var data = new ShapeData { Geometry = Geometry(shape.Type) };
            data.FlipH = (shape.Flags & 0x40) != 0; data.FlipV = (shape.Flags & 0x80) != 0;
            data.Rotation = Math.Round((int)Property(0x0004, 0) / 65536.0, 2) % 360;
            if (data.Geometry != "line") data.Fill = Switch(0x01BF, 4, true) ? ArtColour(Property(0x0181, 0xFFFFFF)) : null;
            data.Line = Switch(0x01FF, 3, true) ? ArtColour(Property(0x01C0, 0)) : null;
            data.LineWidth = Math.Clamp(Math.Round(Property(0x01CB, 9525) / 9525.0, 2), 0.5, 20);
            data.Dash = Property(0x01CE, 0) switch { 0 => "", 2 or 5 => "dot", _ => "dash" };
            data.StartArrow = Property(0x01D0, 0) != 0;
            data.EndArrow = Property(0x01D1, 0) != 0;
            if (text is null) return data;
            // TXO flags: horizontal alignment in bits 1-3 (1 left, 2 centre, 3 right), vertical in bits 4-6 (1 top, 2 middle, 3 bottom).
            string align = ((text.Flags >> 1) & 7) switch { 2 => "ctr", 3 => "r", _ => "l" };
            data.VAlign = ((text.Flags >> 4) & 7) switch { 2 => "ctr", 3 => "b", _ => "t" };
            int start = 0;
            foreach (string line in text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Take(200))
            {
                int fontIndex = text.Runs.Where(r => r.Char <= start).Select(r => r.Font).DefaultIfEmpty(0).Last();
                if (fontIndex >= 4) fontIndex--;                                       // BIFF has no font 4
                var font = fontIndex >= 0 && fontIndex < fonts.Count ? fonts[fontIndex] : default;
                data.Paragraphs.Add(new ShapeParagraph
                {
                    Text = line, Align = align, Bold = font.Bold, Italic = font.Italic,
                    Size = font.Height > 0 ? Math.Round(font.Height / 20.0, 2) : 10,
                    Color = font.Colour is > 0 and < 64 && Colour(font.Colour) is { } colour ? colour : "#000000"
                });
                start += line.Length + 1;
            }
            while (data.Paragraphs.Count > 0 && data.Paragraphs[^1].Text.Length == 0) data.Paragraphs.RemoveAt(data.Paragraphs.Count - 1);
            return data;
        }

        // The drawing group's picture store: one entry per stored picture (its bytes, or null and whether it is in a
        // format the page cannot show, such as EMF or WMF).
        private static List<(byte[]? Bytes, bool Unsupported)> BlipStore(byte[] group)
        {
            var list = new List<(byte[]?, bool)>();
            var ignored = new List<Shape>();
            Walk(group, 0, group.Length, 0, null, ignored, (type, instance, body, stop, _) =>
            {
                if (type == 0xF007 && list.Count < 10_000) list.Add(Blip(group, body, stop));   // FBSE, inside the picture store
            });
            return list;
        }

        private static (byte[]? Bytes, bool Unsupported) Blip(byte[] d, int body, int stop)
        {
            if (stop - body < 36) return (null, false);
            int b = body + 36 + d[body + 33];                                    // after the entry's header and name
            if (b + 8 > stop) return (null, false);                             // not stored here
            int instance = U16(d, b) >> 4, type = U16(d, b + 2);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b + 4));
            int p = b + 8, end = (int)Math.Min(stop, p + length);
            switch (type)
            {
                case 0xF01D or 0xF02A: p += (instance is 0x46B or 0x6E3 ? 32 : 16) + 1; break;   // JPEG
                case 0xF01E: p += (instance == 0x6E1 ? 32 : 16) + 1; break;                     // PNG
                case 0xF01F:                                                                       // DIB: a BMP without its file header
                    p += (instance == 0x7A9 ? 32 : 16) + 1;
                    return p < end ? (Bitmap(d.AsSpan(p, end - p)), false) : (null, false);
                case 0xF01A or 0xF01B:                                                            // EMF, WMF: drawn into a PNG
                    {
                        p += (instance is 0x3D5 or 0x217 ? 32 : 16);
                        if (p + 34 > end) return (null, true);
                        int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
                        long widthEmu = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 20)), heightEmu = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 24));
                        int saved = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 28));
                        bool compressed = d[p + 32] == 0;
                        p += 34;
                        if (size is <= 0 or > 64 * 1024 * 1024 || saved <= 0 || p + saved > end) return (null, true);
                        byte[] metafile;
                        if (compressed)
                        {
                            try
                            {
                                using var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(d, p, saved), System.IO.Compression.CompressionMode.Decompress);
                                metafile = new byte[size]; zlib.ReadAtLeast(metafile, size, throwOnEndOfStream: false);
                            }
                            catch (InvalidDataException) { return (null, true); }
                        }
                        else metafile = d[p..(p + saved)];
                        var png = Metafiles.ToPng(metafile, (int)(widthEmu / 360), (int)(heightEmu / 360));   // EMU to hundredths of a millimetre
                        return png is null ? (null, true) : (png, false);
                    }
                default: return (null, true);                                                     // PICT, TIFF
            }
            return p < end ? (d[p..end], false) : (null, false);
        }

        // A device-independent bitmap with the BMP file header in front, so the page can show it.
        private static byte[]? Bitmap(ReadOnlySpan<byte> dib)
        {
            if (dib.Length < 12) return null;
            int header = BinaryPrimitives.ReadInt32LittleEndian(dib);
            if (header < 12 || header > dib.Length) return null;
            int bits = header == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]) : BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            int compression = header >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(dib[16..]) : 0;
            int used = header >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(dib[32..]) : 0;
            long colours = used > 0 ? Math.Min(used, 256) : bits is > 0 and <= 8 ? 1 << bits : 0;
            int masks = header == 40 && compression == 3 ? 12 : header == 40 && compression == 6 ? 16 : 0;
            long offset = 14 + header + masks + colours * (header == 12 ? 3 : 4);
            if (offset > 14 + dib.Length) return null;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), (int)offset);
            dib.CopyTo(bmp.AsSpan(14));
            return bmp;
        }
    }
}
