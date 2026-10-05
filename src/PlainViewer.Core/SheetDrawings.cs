using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
namespace PlainViewer.Core;

// Pictures and charts placed on an .xlsx worksheet (its drawing part). Pictures stored in the file are checked by
// ImageFiles (format from the bytes, size limits) and written to the work folder for the grid page, which decodes them
// in its sandbox; linked pictures (stored elsewhere) are never fetched. Charts become ChartData from the values saved
// in the chart itself, never recalculated from the cells. Shapes and text boxes become ShapeData (outline, colours, text);
// the shapes and pictures of a group are placed by the group's mapping of their coordinates (rotated groups are drawn
// unrotated).
internal static class SheetDrawings
{
    private const double Emu = 9525;                          // EMUs per pixel at 96 dpi
    public const int MaxPictures = 200, MaxSeries = 50, MaxPoints = 10_000;
    public const long MaxPictureBytes = 20L * 1024 * 1024, MaxTotalBytes = 64L * 1024 * 1024;

    public sealed class Budget { public int Pictures = MaxPictures; public long Bytes = MaxTotalBytes; public int Skipped, Unsupported, Linked; }

    public static List<SheetPicture> Read(string drawingPart, Func<string, Dictionary<string, (string Type, string Target)>> relationships,
        Func<string, XmlReader> open, Func<string, ZipArchiveEntry?> entry, string? folder, int sheet, Budget budget, IReadOnlyList<string> theme)
    {
        var result = new List<SheetPicture>();
        var rels = relationships(drawingPart);
        XDocument drawing;
        using (var reader = open(drawingPart)) drawing = XDocument.Load(reader);
        foreach (var anchor in drawing.Root?.Elements().Where(e => e.Name.LocalName is "twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor") ?? [])
        {
            if (result.Count >= MaxPictures) break;
            var placed = Place(anchor);
            if (placed is null) continue;
            var content = anchor.Elements().FirstOrDefault(e => e.Name.LocalName is "pic" or "graphicFrame" or "grpSp" or "sp" or "cxnSp");
            if (content?.Name.LocalName == "pic")
            {
                var blip = content.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip");
                placed.Description = Attribute(content.Descendants().FirstOrDefault(e => e.Name.LocalName == "cNvPr"), "descr") ?? "";
                string? embed = blip?.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
                if (embed is null) { if (blip?.Attributes().Any(a => a.Name.LocalName == "link") == true) budget.Linked++; continue; }
                if (!rels.TryGetValue(embed, out var rel) || entry(rel.Target) is not { } media) continue;
                if (!Fits(media.Length, budget)) continue;
                byte[] bytes = new byte[media.Length];
                using (var stream = media.Open()) stream.ReadExactly(bytes);
                AddPicture(bytes, placed, folder, sheet, budget, result);
            }
            else if (content?.Name.LocalName == "graphicFrame" && content.Descendants().FirstOrDefault(e => e.Name.LocalName == "relIds") is { } diagram)
            {
                // SmartArt: Excel keeps a drawn copy of the diagram as ordinary shapes (the "diagram drawing" part), in
                // the frame's own space; it is shown like a group. Found through the data part's dataModelExt, or the
                // drawing's only diagram drawing.
                string? drawn = null;
                if (Attribute(diagram, "dm") is { } dataId && rels.TryGetValue(dataId, out var data) && entry(data.Target) is not null)
                {
                    using var reader = open(data.Target);
                    var model = XDocument.Load(reader);
                    if (model.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt") is { } ext && Attribute(ext, "relId") is { } drawingId &&
                        rels.TryGetValue(drawingId, out var target)) drawn = target.Target;
                }
                drawn ??= rels.Values.Where(r => r.Type.EndsWith("/diagramDrawing", StringComparison.Ordinal)).Select(r => r.Target).SingleOrDefaultIfMany();
                if (drawn is null || entry(drawn) is null) { budget.Unsupported++; continue; }
                XDocument shapes;
                using (var reader = open(drawn)) shapes = XDocument.Load(reader);
                var frameSize = Point(Child(Child(content, "xfrm"), "ext"), "cx", "cy");
                if (shapes.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree") is { } tree && frameSize.X > 0 && frameSize.Y > 0)
                    Group(tree, placed, [0, 0, 1, 1], 0, ((0, 0), frameSize));
            }
            else if (content?.Name.LocalName == "graphicFrame")
            {
                var chartRef = content.Descendants().FirstOrDefault(e => e.Name.LocalName == "chart");
                string? id = chartRef?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                if (id is null || !rels.TryGetValue(id, out var rel) || entry(rel.Target) is null) continue;
                XDocument chart;
                using (var reader = open(rel.Target)) chart = XDocument.Load(reader);
                placed.Chart = ReadChart(chart, theme);
                placed.Description = placed.Chart.Title;
                result.Add(placed);
            }
            else if (content?.Name.LocalName is "sp" or "cxnSp")
            {
                placed.Shape = ReadShape(content, theme);
                placed.Description = string.Join(" ", placed.Shape.Paragraphs.Select(p => p.Text)).Trim();
                if (placed.Description.Length == 0) placed.Description = Attribute(content.Descendants().FirstOrDefault(e => e.Name.LocalName == "cNvPr"), "descr") ?? "";
                result.Add(placed);
            }
            else if (content?.Name.LocalName == "grpSp") Group(content, placed, [0, 0, 1, 1], 0);
        }
        return result;

        // A group's shapes and pictures, each with its box within the anchor (frame: the group's box as fractions of it).
        // space: the children's coordinate space when the group has no transform of its own (SmartArt).
        void Group(XElement group, SheetPicture anchorAt, double[] frame, int depth, ((double X, double Y) Offset, (double X, double Y) Size)? space = null)
        {
            var transform = Child(Child(group, "grpSpPr"), "xfrm");
            var (offset, size) = (Point(Child(transform, "off"), "x", "y"), Point(Child(transform, "ext"), "cx", "cy"));
            var (childOffset, childSize) = (Point(Child(transform, "chOff"), "x", "y"), Point(Child(transform, "chExt"), "cx", "cy"));
            if (childSize.X <= 0 || childSize.Y <= 0) (childOffset, childSize) = space is { } given ? given : (offset, size);
            foreach (var child in group.Elements().Where(e => e.Name.LocalName is "sp" or "cxnSp" or "pic" or "grpSp").Take(500))
            {
                if (result.Count >= MaxPictures) return;
                var box = Child(Child(child, child.Name.LocalName == "grpSp" ? "grpSpPr" : "spPr"), "xfrm");
                var (at, extent) = (Point(Child(box, "off"), "x", "y"), Point(Child(box, "ext"), "cx", "cy"));
                double[] part = childSize.X > 0 && childSize.Y > 0
                    ? [frame[0] + (at.X - childOffset.X) / childSize.X * frame[2], frame[1] + (at.Y - childOffset.Y) / childSize.Y * frame[3],
                       extent.X / childSize.X * frame[2], extent.Y / childSize.Y * frame[3]]
                    : frame;
                if (!part.All(double.IsFinite)) continue;
                if (child.Name.LocalName == "grpSp") { if (depth < 8) Group(child, anchorAt, part, depth + 1); continue; }
                var piece = new SheetPicture
                {
                    Row = anchorAt.Row, Column = anchorAt.Column, RowOffset = anchorAt.RowOffset, ColumnOffset = anchorAt.ColumnOffset,
                    ToRow = anchorAt.ToRow, ToColumn = anchorAt.ToColumn, ToRowOffset = anchorAt.ToRowOffset, ToColumnOffset = anchorAt.ToColumnOffset,
                    Width = anchorAt.Width, Height = anchorAt.Height, Part = part
                };
                if (child.Name.LocalName == "pic")
                {
                    var blip = child.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip");
                    piece.Description = Attribute(child.Descendants().FirstOrDefault(e => e.Name.LocalName == "cNvPr"), "descr") ?? "";
                    string? embed = blip?.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
                    if (embed is null) { if (blip?.Attributes().Any(a => a.Name.LocalName == "link") == true) budget.Linked++; continue; }
                    if (!rels.TryGetValue(embed, out var rel) || entry(rel.Target) is not { } media || !Fits(media.Length, budget)) continue;
                    byte[] bytes = new byte[media.Length];
                    using (var stream = media.Open()) stream.ReadExactly(bytes);
                    AddPicture(bytes, piece, folder, sheet, budget, result);
                    continue;
                }
                piece.Shape = ReadShape(child, theme);
                piece.Description = string.Join(" ", piece.Shape.Paragraphs.Select(p => p.Text)).Trim();
                result.Add(piece);
            }
        }
    }

    private static string? SingleOrDefaultIfMany(this IEnumerable<string> items) { var list = items.Distinct().Take(2).ToList(); return list.Count == 1 ? list[0] : null; }

    private static (double X, double Y) Point(XElement? element, string x, string y) => (Number(Attribute(element, x)), Number(Attribute(element, y)));

    // Whether a picture of this many bytes may still be read (counted as skipped if not).
    public static bool Fits(long length, Budget budget)
    {
        if (length <= MaxPictureBytes && length <= budget.Bytes && budget.Pictures > 0) return true;
        budget.Skipped++;
        return false;
    }

    // A picture's bytes, checked by ImageFiles (format from the bytes, pixel limit) and written to the work folder as
    // media-<sheet>-<n>.<type>, the only names the app serves. Formats the page cannot show are counted, not added.
    public static void AddPicture(byte[] bytes, SheetPicture placed, string? folder, int sheet, Budget budget, List<SheetPicture> result)
    {
        // EMF and WMF pictures are drawn into a PNG in the worker (Metafiles).
        if (Metafiles.IsMetafile(bytes)) { if (Metafiles.ToPng(bytes) is { } drawn) bytes = drawn; else { budget.Unsupported++; return; } }
        if (!Fits(bytes.Length, budget)) return;
        var picture = ImageFiles.Identify(bytes);
        if (picture is null || picture.Format is "SVG" or "HEIF" || (long)picture.Width * picture.Height > ImageFiles.PixelLimit) { budget.Unsupported++; return; }
        budget.Bytes -= bytes.Length; budget.Pictures--;
        string name = $"media-{sheet}-{result.Count}.{picture.ContentType.Split('/')[1].Replace("x-icon", "ico")}";
        if (folder is not null) File.WriteAllBytes(Path.Combine(folder, name), bytes);
        placed.Media = name;
        result.Add(placed);
    }

    // The workbook's notices about pictures that are not shown.
    public static IEnumerable<string> Notes(Budget budget)
    {
        if (budget.Unsupported > 0) yield return $"{budget.Unsupported} picture{(budget.Unsupported == 1 ? " is" : "s are")} in a format this viewer cannot show (for example PICT, or a damaged EMF or WMF) and {(budget.Unsupported == 1 ? "is" : "are")} left out.";
        if (budget.Skipped > 0) yield return $"{budget.Skipped} picture{(budget.Skipped == 1 ? " is" : "s are")} left out because the workbook's pictures are larger than this viewer shows at once.";
        if (budget.Linked > 0) yield return $"{budget.Linked} linked picture{(budget.Linked == 1 ? " is" : "s are")} stored outside this file and {(budget.Linked == 1 ? "is" : "are")} not loaded.";
    }

    // Grid geometry shared with the page (sheet.js), in Excel pixels: a column is width × 7 + 5 pixels, a row its height
    // in points × 4/3 (the page scales both to its own row size).
    public static double ColumnPixels(SheetData sheet, int column)
    {
        double width = column < sheet.ColumnWidths.Count ? sheet.ColumnWidths[column] : 8.43;
        return width <= 0 ? 0 : Math.Round(width * 7 + 5);
    }

    public static double RowPixels(SheetData sheet, int row) =>
        (sheet.RowHeights.TryGetValue(row + 1, out double points) ? points : sheet.DefaultRowHeight) * 4 / 3;

    // A position in pixels from the sheet's top-left corner as a cell and an offset within it.
    public static (int Column, double ColumnOffset, int Row, double RowOffset) CellAt(SheetData sheet, double x, double y)
    {
        int column = 0;
        while (column < Spreadsheets.MaxColumns - 1 && x >= ColumnPixels(sheet, column)) { x -= ColumnPixels(sheet, column); column++; }
        int row = 0;
        y = Math.Max(0, y);
        while (row < Spreadsheets.MaxStoredRows - 1 && y >= RowPixels(sheet, row)) { y -= RowPixels(sheet, row); row++; }
        return (column, Math.Max(0, x), row, y);
    }

    // A drawing placed by a cell and an offset that may run past that cell, with a size instead of an end cell (.ods
    // shapes, group members and lines): its start and end as cells with offsets inside them, as the page expects.
    public static void Normalize(SheetData sheet, SheetPicture picture)
    {
        double x = picture.ColumnOffset, y = picture.RowOffset;
        for (int c = 0; c < picture.Column && c < Spreadsheets.MaxColumns; c++) x += ColumnPixels(sheet, c);
        for (int r = 0; r < picture.Row && r < Spreadsheets.MaxStoredRows; r++) y += RowPixels(sheet, r);
        (picture.Column, picture.ColumnOffset, picture.Row, picture.RowOffset) = CellAt(sheet, x, y);
        if (picture.ToRow < 0 && picture.Width >= 0 && picture.Height >= 0)
            (picture.ToColumn, picture.ToColumnOffset, picture.ToRow, picture.ToRowOffset) = CellAt(sheet, x + picture.Width, y + picture.Height);
    }

    private static string? Attribute(XElement? element, string name) => element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
    private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static double Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : 0;
    private static int Cell(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 0, Spreadsheets.MaxStoredRows) : 0;

    // The anchor as a cell position plus offsets, and either an end cell or a size in pixels.
    private static SheetPicture? Place(XElement anchor)
    {
        var picture = new SheetPicture();
        if (Child(anchor, "from") is { } from)
        {
            picture.Column = Cell(Child(from, "col")?.Value); picture.ColumnOffset = Number(Child(from, "colOff")?.Value) / Emu;
            picture.Row = Cell(Child(from, "row")?.Value); picture.RowOffset = Number(Child(from, "rowOff")?.Value) / Emu;
        }
        else if (Child(anchor, "pos") is { } pos)
        { picture.ColumnOffset = Number(Attribute(pos, "x")) / Emu; picture.RowOffset = Number(Attribute(pos, "y")) / Emu; }
        else return null;
        if (Child(anchor, "to") is { } to)
        {
            picture.ToColumn = Cell(Child(to, "col")?.Value); picture.ToColumnOffset = Number(Child(to, "colOff")?.Value) / Emu;
            picture.ToRow = Cell(Child(to, "row")?.Value); picture.ToRowOffset = Number(Child(to, "rowOff")?.Value) / Emu;
        }
        else if (Child(anchor, "ext") is { } ext)
        { picture.Width = Number(Attribute(ext, "cx")) / Emu; picture.Height = Number(Attribute(ext, "cy")) / Emu; }
        else return null;
        return picture;
    }

    // A chart part (c:chartSpace) as its saved data. The first kind of chart in its plot area is used.
    public static ChartData ReadChart(XDocument document, IReadOnlyList<string> theme)
    {
        var data = new ChartData();
        var chart = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "chart" && e.Parent?.Name.LocalName == "chartSpace");
        if (chart is null) { data.Notice = "This chart could not be read."; return data; }
        if (Child(chart, "title") is { } title) data.Title = TitleText(title);
        var plot = Child(chart, "plotArea");
        string[] known = ["barChart", "bar3DChart", "lineChart", "line3DChart", "areaChart", "area3DChart", "pieChart", "pie3DChart", "ofPieChart", "doughnutChart",
            "scatterChart", "radarChart", "bubbleChart", "stockChart"];
        var kinds = plot?.Elements().Where(e => e.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal)).ToList() ?? [];
        var kind = kinds.FirstOrDefault(e => known.Contains(e.Name.LocalName));
        if (kind is null) { data.Notice = kinds.Count == 0 ? "This chart has no data to show." : "This kind of chart is not shown in this version."; return data; }
        static string TypeOf(XElement part) => part.Name.LocalName switch
        {
            "barChart" or "bar3DChart" => Attribute(Child(part, "barDir"), "val") == "bar" ? "bar" : "column",
            "lineChart" or "line3DChart" => "line", "areaChart" or "area3DChart" => "area",
            "doughnutChart" => "doughnut", "scatterChart" => "scatter", "radarChart" => "radar", "bubbleChart" => "bubble", "stockChart" => "stock", _ => "pie"
        };
        string name = kind.Name.LocalName;
        string grouping = Attribute(Child(kind, "grouping"), "val") ?? "";
        data.Type = TypeOf(kind);
        data.Filled = data.Type == "radar" && Attribute(Child(kind, "radarStyle"), "val") == "filled";
        // Combined charts: columns (or bars) with lines or areas over the same categories are drawn together; other
        // combinations show their first part with a notice.
        var parts = new List<XElement> { kind };
        foreach (var other in kinds.Where(k => k != kind))
        {
            string otherType = known.Contains(other.Name.LocalName) ? TypeOf(other) : "";
            if (data.Type is "column" or "bar" or "line" or "area" && otherType is "column" or "bar" or "line" or "area" && (otherType == "bar") == (data.Type == "bar")) parts.Add(other);
            else data.Notice = "Only the first part of this combined chart is shown.";
        }
        data.Stacked = grouping is "stacked" or "percentStacked";
        data.Percent = grouping == "percentStacked";
        int seriesIndex = 0;
        var labelParts = new List<(bool Value, bool Percent, bool Category, string? Format)>();
        foreach (var (series, part) in parts.SelectMany(p => p.Elements().Where(e => e.Name.LocalName == "ser").Select(s => (s, p))).Take(MaxSeries))
        {
            // Data labels: the series' own settings, else the chart's; a label's number format is its own or the values'.
            var own = Child(series, "dLbls"); var shared = Child(part, "dLbls");   // the settings of the series' own part of a combined chart
            bool Shows(string flagName) => !Deleted(own) && (Child(own, flagName) ?? (own is null && !Deleted(shared) ? Child(shared, flagName) : null)) is { } flag && Attribute(flag, "val") is "1" or "true";
            var labelFormat = Child(own ?? shared, "numFmt");
            labelParts.Add((Shows("showVal"), Shows("showPercent"), Shows("showCatName"),
                labelFormat is not null && Attribute(labelFormat, "sourceLinked") is not ("1" or "true") ? Attribute(labelFormat, "formatCode")
                    : Child(series, data.Type is "scatter" or "bubble" ? "yVal" : "val")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "formatCode")?.Value));
            var item = new ChartSeries { Name = Text(Child(series, "tx")) ?? $"Series {seriesIndex + 1}", Color = Colour(Child(series, "spPr"), theme) };
            if (part != kind) item.Type = TypeOf(part);
            if (data.Type is "scatter" or "bubble")
            {
                item.X = Values(Child(series, "xVal"));
                item.Values = Values(Child(series, "yVal"));
                if (data.Type == "bubble") item.Sizes = Values(Child(series, "bubbleSize"));
            }
            else
            {
                item.Values = Values(Child(series, "val"));
                if (data.Categories.Count == 0) data.Categories = Labels(Child(series, "cat"));
            }
            data.Series.Add(item);
            seriesIndex++;
        }
        int points = data.Series.Count == 0 ? 0 : data.Series.Max(s => s.Values.Count);
        while (data.Categories.Count < points) data.Categories.Add((data.Categories.Count + 1).ToString(CultureInfo.InvariantCulture));
        for (int s = 0; s < data.Series.Count; s++)
            data.Series[s].PointLabels = PointLabels(data, data.Series[s], labelParts[s].Value, labelParts[s].Percent, labelParts[s].Category, labelParts[s].Format);
        // Axis titles; on a scatter chart the axis along the bottom (or top) is the X axis.
        foreach (var axis in plot!.Elements().Where(e => e.Name.LocalName is "catAx" or "dateAx" or "valAx").Take(4))
        {
            if (Child(axis, "title") is not { } axisTitle || Attribute(Child(axis, "delete"), "val") is "1" or "true") continue;
            string text = TitleText(axisTitle);
            bool category = data.Type is "scatter" or "bubble" ? Attribute(Child(axis, "axPos"), "val") is "b" or "t" : axis.Name.LocalName != "valAx";
            if (category && data.CategoryTitle.Length == 0) data.CategoryTitle = text;
            else if (!category && data.ValueTitle.Length == 0) data.ValueTitle = text;
        }
        return data;
    }

    private static bool Deleted(XElement? labels) => Attribute(Child(labels, "delete"), "val") is "1" or "true";

    // A title's text: its rich text runs, or the cached text of a reference.
    private static string TitleText(XElement title)
    {
        string rich = string.Concat(title.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)).Trim();
        return rich.Length > 0 ? rich : title.Descendants().FirstOrDefault(e => e.Name.LocalName == "v")?.Value.Trim() ?? "";
    }

    // Data labels as shown: the category name, the value in its number format and (pie and doughnut charts) the share of
    // the total, joined by ", "; "" for a point without a value. Empty when the series shows no labels.
    internal static List<string> PointLabels(ChartData chart, ChartSeries series, bool value, bool percent, bool category, string? format)
    {
        bool round = chart.Type is "pie" or "doughnut";
        if (!value && !category && !(percent && round)) return [];
        double total = series.Values.Where(v => v > 0).Sum(v => v!.Value);
        var labels = new List<string>();
        for (int i = 0; i < series.Values.Count; i++)
        {
            if (series.Values[i] is not double number) { labels.Add(""); continue; }
            var parts = new List<string>();
            if (category && i < chart.Categories.Count) parts.Add(chart.Categories[i]);
            if (value) parts.Add(FormatNumber(number, format));
            if (percent && round && total > 0) parts.Add((Math.Max(0, number) / total).ToString("0%", CultureInfo.CurrentCulture));
            labels.Add(string.Join(", ", parts));
        }
        return labels;
    }

    private static string FormatNumber(double value, string? format)
    {
        if (format is { Length: > 0 } && format != "General")
            try { var f = new ExcelNumberFormat.NumberFormat(format); if (f.IsValid) return f.Format(value, CultureInfo.CurrentCulture); }
            catch (Exception) { }
        return value.ToString("G15", CultureInfo.CurrentCulture);
    }

    // A series name: literal text (c:v) or the cached text of a reference (c:strRef/c:strCache).
    private static string? Text(XElement? tx)
    {
        if (tx is null) return null;
        var value = tx.Descendants().FirstOrDefault(e => e.Name.LocalName == "v");
        return value?.Value is { Length: > 0 } text ? text : null;
    }

    // Cached points (c:pt idx="n"), in order of their index; missing points are null.
    private static List<(int Index, string Value)> Points(XElement? container)
    {
        var cache = container?.Descendants().FirstOrDefault(e => e.Name.LocalName is "numCache" or "strCache" or "numLit" or "strLit");
        if (cache is null) return [];
        return cache.Elements().Where(e => e.Name.LocalName == "pt")
            .Select(e => (Index: Cell(Attribute(e, "idx")), Value: Child(e, "v")?.Value ?? ""))
            .Where(p => p.Index < MaxPoints).OrderBy(p => p.Index).ToList();
    }

    private static List<double?> Values(XElement? container)
    {
        var points = Points(container);
        var values = new List<double?>();
        foreach (var (index, value) in points)
        {
            while (values.Count < index) values.Add(null);
            values.Add(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null);
        }
        return values;
    }

    // Category labels; numbers keep the cache's number format when it has one (dates, for example).
    private static List<string> Labels(XElement? container)
    {
        var points = Points(container);
        string? format = container?.Descendants().FirstOrDefault(e => e.Name.LocalName == "formatCode")?.Value;
        var labels = new List<string>();
        foreach (var (index, value) in points)
        {
            while (labels.Count < index) labels.Add("");
            if (format is { Length: > 0 } && format != "General" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                try { var f = new ExcelNumberFormat.NumberFormat(format); labels.Add(f.IsValid ? f.Format(number, CultureInfo.CurrentCulture) : value); }
                catch (Exception) { labels.Add(value); }
            }
            else labels.Add(value);
        }
        return labels;
    }

    // Shape outlines drawn as such; any other preset is drawn as a rectangle (with its text).
    public static readonly string[] Geometries = ["rect", "roundRect", "ellipse", "triangle", "rtTriangle", "diamond", "parallelogram", "hexagon",
        "line", "rightArrow", "leftArrow", "upArrow", "downArrow"];

    // A shape (xdr:sp) or connector (xdr:cxnSp): outline, fill and line (its own, else from its style's references to
    // the theme), rotation and flips, and the text of a text box or shape.
    internal static ShapeData ReadShape(XElement shape, IReadOnlyList<string> theme)
    {
        var properties = Child(shape, "spPr");
        var style = Child(shape, "style");
        string preset = Attribute(Child(properties, "prstGeom"), "prst") ?? "rect";
        var data = new ShapeData
        {
            Geometry = preset is "straightConnector1" or "bentConnector2" or "bentConnector3" or "curvedConnector3" ? "line" : Geometries.Contains(preset) ? preset : "rect"
        };
        if (Child(properties, "xfrm") is { } transform)
        {
            data.Rotation = Math.Round(Number(Attribute(transform, "rot")) / 60000, 2) % 360;
            data.FlipH = Attribute(transform, "flipH") is "1" or "true";
            data.FlipV = Attribute(transform, "flipV") is "1" or "true";
        }
        // Fill: none, a solid colour, a gradient's first colour, or the style's fill colour.
        if (data.Geometry != "line")
        {
            if (Child(properties, "noFill") is not null) data.Fill = null;
            else if (Child(properties, "solidFill") is { } solid) data.Fill = DrawingColour(solid, theme);
            else if (Child(properties, "gradFill")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "gs") is { } stop) data.Fill = DrawingColour(stop, theme);
            else if (Child(style, "fillRef") is { } fillRef && Number(Attribute(fillRef, "idx")) > 0) data.Fill = DrawingColour(fillRef, theme);
        }
        // Outline: none, its own colour, or the style's line colour; width in EMUs; dashes; arrow ends.
        var line = Child(properties, "ln");
        if (Child(line, "noFill") is not null) data.Line = null;
        else if (Child(line, "solidFill") is { } lineFill) data.Line = DrawingColour(lineFill, theme);
        else if (Child(style, "lnRef") is { } lineRef && Number(Attribute(lineRef, "idx")) > 0) data.Line = DrawingColour(lineRef, theme);
        if (Attribute(line, "w") is { } width) data.LineWidth = Math.Clamp(Math.Round(Number(width) / Emu, 2), 0.5, 20);
        data.Dash = Attribute(Child(line, "prstDash"), "val") switch { "dash" or "sysDash" or "lgDash" or "dashDot" or "lgDashDot" or "lgDashDotDot" or "sysDashDot" or "sysDashDotDot" => "dash", "dot" or "sysDot" => "dot", _ => "" };
        data.StartArrow = Attribute(Child(line, "headEnd"), "type") is { } head && head != "none";
        data.EndArrow = Attribute(Child(line, "tailEnd"), "type") is { } tail && tail != "none";
        // Text: paragraphs of runs, with the first run's look; the style's font colour, else black.
        var body = Child(shape, "txBody");
        string defaultColour = Child(style, "fontRef") is { } fontRef ? DrawingColour(fontRef, theme) ?? "#000000" : "#000000";
        data.VAlign = Attribute(Child(body, "bodyPr"), "anchor") switch { "ctr" => "ctr", "b" => "b", _ => "t" };
        foreach (var paragraph in body?.Elements().Where(e => e.Name.LocalName == "p").Take(200) ?? [])
        {
            var text = new System.Text.StringBuilder();
            foreach (var part in paragraph.Elements())
                if (part.Name.LocalName is "r" or "fld") text.Append(Child(part, "t")?.Value);
                else if (part.Name.LocalName == "br") text.Append('\n');
            var look = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName is "r" or "fld") is { } run ? Child(run, "rPr") : Child(paragraph, "endParaRPr");
            double size = Number(Attribute(look, "sz")) / 100;
            data.Paragraphs.Add(new ShapeParagraph
            {
                Text = text.Length > 4000 ? text.ToString(0, 4000) : text.ToString(),
                Align = Attribute(Child(paragraph, "pPr"), "algn") switch { "ctr" => "ctr", "r" => "r", _ => "l" },
                Bold = Attribute(look, "b") is "1" or "true", Italic = Attribute(look, "i") is "1" or "true",
                Size = size is >= 1 and <= 400 ? size : 11,
                Color = Child(look, "solidFill") is { } runFill ? DrawingColour(runFill, theme) ?? defaultColour : defaultColour
            });
        }
        while (data.Paragraphs.Count > 0 && data.Paragraphs[^1].Text.Length == 0) data.Paragraphs.RemoveAt(data.Paragraphs.Count - 1);
        return data;
    }

    // A DrawingML colour (the element holding srgbClr, schemeClr, sysClr or prstClr) with its shade, tint and luminance
    // changes; null when there is none.
    internal static string? DrawingColour(XElement holder, IReadOnlyList<string> theme)
    {
        var colour = holder.Elements().FirstOrDefault(e => e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr");
        if (colour is null) return null;
        string? value = colour.Name.LocalName switch
        {
            "srgbClr" => WorkbookStyles.Hex(Attribute(colour, "val")),
            "sysClr" => WorkbookStyles.Hex(Attribute(colour, "lastClr")) ?? (Attribute(colour, "val") == "window" ? "#ffffff" : "#000000"),
            "prstClr" => Attribute(colour, "val") switch { "black" => "#000000", "white" => "#ffffff", "red" => "#ff0000", "green" => "#008000", "blue" => "#0000ff", "yellow" => "#ffff00", "gray" => "#808080", _ => null },
            _ when theme.Count >= 10 => Attribute(colour, "val") switch
            {
                "dk1" or "tx1" => theme[0], "lt1" or "bg1" => theme[1], "dk2" or "tx2" => theme[2], "lt2" or "bg2" => theme[3],
                "accent1" => theme[4], "accent2" => theme[5], "accent3" => theme[6], "accent4" => theme[7], "accent5" => theme[8], "accent6" => theme[9], _ => null
            },
            _ => Attribute(colour, "val") switch { "dk1" or "tx1" => "#000000", "lt1" or "bg1" => "#ffffff", _ => null }
        };
        if (value is null) return null;
        foreach (var change in colour.Elements())
        {
            double amount = Number(Attribute(change, "val")) / 100000;
            value = change.Name.LocalName switch
            {
                "shade" => Linear(value, c => c * Math.Clamp(amount, 0, 1)),
                "tint" => Linear(value, c => 1 - (1 - c) * Math.Clamp(amount, 0, 1)),
                "lumMod" => Luminance(value, amount, 0),
                "lumOff" => Luminance(value, 1, amount),
                _ => value
            };
        }
        return value;
    }

    // Shade and tint work on linear light (sRGB with its gamma removed), as Office computes them: accent1 #4f81bd at a
    // 50% shade is #385d8a.
    private static string Linear(string colour, Func<double, double> change)
    {
        string Channel(int at)
        {
            double s = Convert.ToInt32(colour.Substring(at, 2), 16) / 255.0;
            double linear = s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            linear = Math.Clamp(change(linear), 0, 1);
            double back = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return ((int)Math.Round(Math.Clamp(back, 0, 1) * 255)).ToString("x2");
        }
        return "#" + Channel(1) + Channel(3) + Channel(5);
    }

    // HSL lightness × scale + offset (DrawingML's lumMod and lumOff).
    private static string Luminance(string colour, double scale, double offset)
    {
        double r = Convert.ToInt32(colour[1..3], 16) / 255.0, g = Convert.ToInt32(colour[3..5], 16) / 255.0, b = Convert.ToInt32(colour[5..7], 16) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        l = Math.Clamp(l * scale + offset, 0, 1);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t) { t = t < 0 ? t + 1 : t > 1 ? t - 1 : t; return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; }
        int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return s == 0 ? $"#{Byte(l):x2}{Byte(l):x2}{Byte(l):x2}" : $"#{Byte(Hue(h + 1 / 3.0)):x2}{Byte(Hue(h)):x2}{Byte(Hue(h - 1 / 3.0)):x2}";
    }

    // The series' fill (bars, areas, slices) or line colour: an sRGB value or a theme colour.
    private static string? Colour(XElement? shape, IReadOnlyList<string> theme)
    {
        var fill = Child(shape, "solidFill") ?? Child(Child(shape, "ln"), "solidFill");
        var colour = fill?.Elements().FirstOrDefault();
        if (colour is null) return null;
        if (colour.Name.LocalName == "srgbClr") return WorkbookStyles.Hex(Attribute(colour, "val"));
        if (colour.Name.LocalName == "schemeClr" && theme.Count >= 10)
            return Attribute(colour, "val") switch
            {
                "accent1" => theme[4], "accent2" => theme[5], "accent3" => theme[6], "accent4" => theme[7], "accent5" => theme[8], "accent6" => theme[9],
                "tx1" or "dk1" => theme[0], "dk2" => theme[2], _ => null
            };
        return null;
    }
}
