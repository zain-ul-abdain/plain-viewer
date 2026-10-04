using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
namespace PlainViewer.Core;

// Pictures and charts placed on an OpenDocument spreadsheet (draw:frame elements, anchored to a cell or to the sheet).
// Pictures stored in the file are checked and written to the work folder like .xlsx pictures (SheetDrawings); pictures
// referred to outside the file are counted and never opened. A chart is an embedded chart object ("Object N/content.xml")
// whose cached data table (the "local-table" the producing application saved with it) is shown; nothing is recalculated.
internal static class OpenDocumentDrawings
{
    private const string DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string SvgNs = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string XlinkNs = "http://www.w3.org/1999/xlink";
    private const string ChartNs = "urn:oasis:names:tc:opendocument:xmlns:chart:1.0";
    private const string StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    // A frame anchored to a cell. A frame anchored to the sheet is passed with row and column 0; its offsets are then
    // its position on the sheet, which the caller converts to a cell once the column widths are known. Returns the placed picture or chart, or null if the frame holds neither.
    public static SheetPicture? Frame(XElement frame, ZipArchive zip, string? folder, int sheet, SheetDrawings.Budget budget, List<SheetPicture> result, int row, int column)
    {
        if (result.Count >= SheetDrawings.MaxPictures) return null;
        var placed = new SheetPicture
        {
            Row = row, Column = column, RowOffset = Length(Attr(frame, SvgNs, "y")), ColumnOffset = Length(Attr(frame, SvgNs, "x")),
            Width = Length(Attr(frame, SvgNs, "width")), Height = Length(Attr(frame, SvgNs, "height")),
            Description = frame.Element(XName.Get("desc", SvgNs))?.Value ?? frame.Element(XName.Get("title", SvgNs))?.Value ?? ""
        };
        if (Attr(frame, TableNs, "end-cell-address") is { } end && EndCell(end) is var (toRow, toColumn))
        {
            placed.ToRow = toRow; placed.ToColumn = toColumn;
            placed.ToRowOffset = Length(Attr(frame, TableNs, "end-y")); placed.ToColumnOffset = Length(Attr(frame, TableNs, "end-x"));
        }
        if (frame.Element(XName.Get("object", DrawNs)) is { } embedded)
        {
            // Only a chart object inside this file; its replacement picture is not used.
            if (Inside(Attr(embedded, XlinkNs, "href")) is not { } part || zip.GetEntry(part + "/content.xml") is not { } content) return null;
            XDocument chart;
            using (var reader = XmlReader.Create(content.Open(), Settings)) chart = XDocument.Load(reader);
            if (chart.Descendants(XName.Get("chart", ChartNs)).FirstOrDefault() is not { } root) return null;
            placed.Chart = ReadChart(chart, root);
            if (placed.Description.Length == 0) placed.Description = placed.Chart.Title;
            result.Add(placed);
            return placed;
        }
        bool linked = false, unsupported = false;
        foreach (var image in frame.Elements(XName.Get("image", DrawNs)))
        {
            // A picture stored in the file, or embedded as base64; an address outside the file is never opened.
            byte[]? bytes = null;
            if (image.Element(XName.Get("binary-data", OfficeNs)) is { } binary)
            {
                if (binary.Value.Length / 4L * 3 > SheetDrawings.MaxPictureBytes) { budget.Skipped++; return null; }
                try { bytes = Convert.FromBase64String(binary.Value); } catch (FormatException) { unsupported = true; continue; }
            }
            else if (Attr(image, XlinkNs, "href") is { } href)
            {
                if (Inside(href) is not { } path || zip.GetEntry(path) is not { } entry) { linked = true; continue; }
                if (!SheetDrawings.Fits(entry.Length, budget)) return null;
                bytes = new byte[entry.Length];
                using (var stream = entry.Open()) stream.ReadExactly(bytes);
            }
            if (bytes is null) continue;
            if (Metafiles.IsMetafile(bytes) && Metafiles.ToPng(bytes) is { } drawn) bytes = drawn;   // EMF and WMF, drawn into a PNG
            var picture = ImageFiles.Identify(bytes);
            // LibreOffice stores an SVG picture with a PNG copy after it: the first picture the page can show is used.
            if (picture is null || picture.Format is "SVG" or "HEIF") { unsupported = true; continue; }
            int before = result.Count;
            SheetDrawings.AddPicture(bytes, placed, folder, sheet, budget, result);
            return result.Count > before ? placed : null;
        }
        if (linked) budget.Linked++;
        else if (unsupported) budget.Unsupported++;
        return null;
    }

    // Graphic, paragraph and text styles that shapes use: styles.xml's styles and graphic default, then content.xml's
    // automatic styles (read up to the document body).
    public sealed class ShapeStyles
    {
        public readonly Dictionary<string, XElement> Named = [];
        public XElement? Default;
    }

    public static ShapeStyles ReadShapeStyles(ZipArchive zip)
    {
        var styles = new ShapeStyles();
        foreach (var name in new[] { "styles.xml", "content.xml" })
        {
            if (zip.GetEntry(name) is not { } entry) continue;
            using var reader = XmlReader.Create(entry.Open(), Settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "body" && reader.NamespaceURI == OfficeNs) break;
                if (reader.NamespaceURI != StyleNs || reader.LocalName is not ("style" or "default-style")) continue;
                string? family = reader.GetAttribute("family", StyleNs), styleName = reader.GetAttribute("name", StyleNs);
                if (family is not ("graphic" or "paragraph" or "text")) continue;
                bool isDefault = reader.LocalName == "default-style";
                XElement style;
                using (var subtree = reader.ReadSubtree()) style = XElement.Load(subtree);
                if (isDefault && family == "graphic") styles.Default = style;
                else if (styleName is not null && styles.Named.Count < 20_000) styles.Named[styleName] = style;
            }
        }
        return styles;
    }

    // A style property, following parent styles and finally the graphic default.
    private static string? Property(ShapeStyles styles, string? style, string properties, string ns, string name)
    {
        for (int depth = 0; style is not null && depth < 10 && styles.Named.TryGetValue(style, out var element); depth++)
        {
            if (element.Element(XName.Get(properties, StyleNs))?.Attribute(XName.Get(name, ns))?.Value is { } value) return value;
            style = Attr(element, StyleNs, "parent-style-name");
        }
        return styles.Default?.Element(XName.Get(properties, StyleNs))?.Attribute(XName.Get(name, ns))?.Value;
    }

    private const string FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    // Shapes (custom shapes, rectangles, ellipses, lines, connectors and the shapes of groups) anchored at a cell, or
    // on the sheet (row and column 0; the caller converts their positions to cells). Returns what was placed.
    public static List<SheetPicture> Shapes(XElement element, ShapeStyles styles, List<SheetPicture> result, int row, int column, int depth = 0)
    {
        var placed = new List<SheetPicture>();
        if (result.Count >= SheetDrawings.MaxPictures) return placed;
        if (element.Name.LocalName == "g")
        {
            if (depth < 8) foreach (var child in element.Elements().Take(500)) placed.AddRange(Shapes(child, styles, result, row, column, depth + 1));
            return placed;
        }
        if (element.Name.NamespaceName != DrawNs || element.Name.LocalName is not ("custom-shape" or "rect" or "ellipse" or "line" or "connector")) return placed;
        string? style = Attr(element, DrawNs, "style-name");
        string Graphic(string name, string ns = DrawNs) => Property(styles, style, "graphic-properties", ns, name) ?? "";
        var shape = new ShapeData();
        var picture = new SheetPicture { Row = row, Column = column, Shape = shape };
        if (element.Name.LocalName is "line" or "connector")
        {
            double x1 = Length(Attr(element, SvgNs, "x1")), y1 = Length(Attr(element, SvgNs, "y1")), x2 = Length(Attr(element, SvgNs, "x2")), y2 = Length(Attr(element, SvgNs, "y2"));
            shape.Geometry = "line";
            (picture.ColumnOffset, picture.RowOffset, picture.Width, picture.Height) = (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            (shape.FlipH, shape.FlipV) = (x2 < x1, y2 < y1);
        }
        else
        {
            (picture.ColumnOffset, picture.RowOffset) = (Length(Attr(element, SvgNs, "x")), Length(Attr(element, SvgNs, "y")));
            (picture.Width, picture.Height) = (Length(Attr(element, SvgNs, "width")), Length(Attr(element, SvgNs, "height")));
            var geometry = element.Element(XName.Get("enhanced-geometry", DrawNs));
            string type = element.Name.LocalName == "custom-shape" ? Attr(geometry ?? element, DrawNs, "type") ?? "" : element.Name.LocalName;
            if (type.StartsWith("ooxml-", StringComparison.Ordinal)) type = type[6..];
            shape.Geometry = type switch
            {
                "rect" or "rectangle" => "rect", "roundRect" or "round-rectangle" => "roundRect", "ellipse" => "ellipse",
                "triangle" or "isosceles-triangle" => "triangle", "rtTriangle" or "right-triangle" => "rtTriangle", "diamond" => "diamond",
                "parallelogram" => "parallelogram", "hexagon" => "hexagon", "rightArrow" or "right-arrow" => "rightArrow", "leftArrow" or "left-arrow" => "leftArrow",
                "upArrow" or "up-arrow" => "upArrow", "downArrow" or "down-arrow" => "downArrow", _ => "rect"
            };
            shape.FlipH = geometry is not null && Attr(geometry, DrawNs, "mirror-horizontal") == "true";
            shape.FlipV = geometry is not null && Attr(geometry, DrawNs, "mirror-vertical") == "true";
            string fill = Graphic("fill");
            shape.Fill = fill is "none" or "bitmap" or "hatch" ? null : WorkbookStyles.Hex(Graphic("fill-color").TrimStart('#'));
            shape.VAlign = Graphic("textarea-vertical-align") switch { "middle" => "ctr", "bottom" => "b", _ => "t" };
        }
        if (Attr(element, TableNs, "end-cell-address") is { } end && EndCell(end) is var (toRow, toColumn) && shape.Geometry != "line")
        {
            (picture.ToRow, picture.ToColumn) = (toRow, toColumn);
            (picture.ToRowOffset, picture.ToColumnOffset) = (Length(Attr(element, TableNs, "end-y")), Length(Attr(element, TableNs, "end-x")));
        }
        string stroke = Graphic("stroke");
        shape.Line = stroke == "none" ? null : WorkbookStyles.Hex(Graphic("stroke-color", SvgNs).TrimStart('#')) ?? "#000000";
        if (Length(Graphic("stroke-width", SvgNs)) is > 0 and var width) shape.LineWidth = Math.Clamp(Math.Round(width, 2), 0.5, 20);
        shape.Dash = stroke != "dash" ? "" : Graphic("stroke-dash").Contains("Dot", StringComparison.OrdinalIgnoreCase) && !Graphic("stroke-dash").Contains("Dash", StringComparison.OrdinalIgnoreCase) ? "dot" : "dash";
        shape.StartArrow = Graphic("marker-start").Length > 0;
        shape.EndArrow = Graphic("marker-end").Length > 0;
        // Text: each paragraph with its alignment and its first span's look (else the paragraph's, else the shape's).
        string? paragraphDefault = Attr(element, DrawNs, "text-style-name");
        foreach (var paragraph in element.Elements(XName.Get("p", TextNs)).Take(200))
        {
            string? paragraphStyle = Attr(paragraph, TextNs, "style-name");
            string? span = paragraph.Elements(XName.Get("span", TextNs)).Select(s => Attr(s, TextNs, "style-name")).FirstOrDefault(s => s is not null);
            string? Text(string name) => Property(styles, span, "text-properties", FoNs, name) ?? Property(styles, paragraphStyle, "text-properties", FoNs, name)
                ?? Property(styles, style, "text-properties", FoNs, name);
            string? align = Property(styles, paragraphStyle, "paragraph-properties", FoNs, "text-align") ?? Property(styles, paragraphDefault, "paragraph-properties", FoNs, "text-align")
                ?? Property(styles, style, "paragraph-properties", FoNs, "text-align");
            double size = Length(Text("font-size")) * 72 / 96;
            shape.Paragraphs.Add(new ShapeParagraph
            {
                Text = ParagraphText(paragraph),
                Align = align switch { "center" => "ctr", "end" or "right" => "r", _ => "l" },
                Bold = Text("font-weight") is "bold" or "600" or "700" or "800" or "900",
                Italic = Text("font-style") is "italic" or "oblique",
                Size = size is >= 1 and <= 400 ? Math.Round(size, 2) : 11,
                Color = WorkbookStyles.Hex(Text("color")?.TrimStart('#')) ?? "#000000"
            });
        }
        while (shape.Paragraphs.Count > 0 && shape.Paragraphs[^1].Text.Length == 0) shape.Paragraphs.RemoveAt(shape.Paragraphs.Count - 1);
        picture.Description = string.Join(" ", shape.Paragraphs.Select(p => p.Text)).Trim();
        result.Add(picture); placed.Add(picture);
        return placed;
    }

    // A paragraph's text with its spaces, tabs and line breaks.
    private static string ParagraphText(XElement paragraph)
    {
        var text = new System.Text.StringBuilder();
        foreach (var node in paragraph.DescendantNodes())
        {
            if (text.Length > 4000) break;
            if (node is XText value && value.Parent?.Name.LocalName is not ("s" or "tab" or "line-break")) text.Append(value.Value);
            else if (node is XElement e && e.Name.NamespaceName == TextNs)
                switch (e.Name.LocalName)
                {
                    case "s": text.Append(' ', int.TryParse(Attr(e, TextNs, "c"), out int c) ? Math.Clamp(c, 1, 100) : 1); break;
                    case "tab": text.Append('\t'); break;
                    case "line-break": text.Append('\n'); break;
                }
        }
        return text.ToString();
    }

    private static readonly XmlReaderSettings Settings = new()
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true };

    private static string? Attr(XElement element, string ns, string name) => element.Attribute(XName.Get(name, ns))?.Value;

    // A path inside the package ("Pictures/x.png", "./Object 1"); null for anything that points elsewhere.
    private static string? Inside(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        string path = href.StartsWith("./", StringComparison.Ordinal) ? href[2..] : href;
        if (path.Contains(':') || path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(p => p is ".." or "") || path.Contains('%')) return null;
        return path;
    }

    // "Sheet1.E22" or "'My sheet'.$E$22" as a zero-based row and column.
    private static (int Row, int Column)? EndCell(string address)
    {
        string cell = address[(address.LastIndexOf('.') + 1)..].Replace("$", "");
        return Spreadsheets.TryCell(cell, out int row, out int column) ? (row - 1, column) : null;
    }

    // "2.5cm", "0.65in" and so on in pixels at 96 dpi; 0 when missing.
    private static double Length(string? value)
    {
        if (value is null) return 0;
        foreach (var (unit, factor) in new[] { ("cm", 96 / 2.54), ("mm", 96 / 25.4), ("in", 96.0), ("pt", 96 / 72.0), ("pc", 16.0), ("px", 1.0) })
            if (value.EndsWith(unit, StringComparison.Ordinal) && double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n))
                return Math.Clamp(n * factor, 0, 1_000_000);
        return 0;
    }

    // The chart's kind, title, series names and colours, and its cached data table (first column: categories, or x
    // values for a scatter chart; then one column per series).
    public static ChartData ReadChart(XDocument document, XElement chart)
    {
        var data = new ChartData();
        var styles = document.Descendants(XName.Get("style", StyleNs))
            .Where(s => Attr(s, StyleNs, "name") is not null).GroupBy(s => Attr(s, StyleNs, "name")!).ToDictionary(g => g.Key, g => g.First());
        string? Property(string? style, string element, string ns, string name) =>
            style is not null && styles.TryGetValue(style, out var s) ? s.Element(XName.Get(element, StyleNs))?.Attribute(XName.Get(name, ns))?.Value : null;
        data.Title = string.Join(" ", chart.Element(XName.Get("title", ChartNs))?.Elements().Select(p => p.Value.Trim()) ?? []).Trim();
        string kind = Attr(chart, ChartNs, "class") ?? "";
        var plot = chart.Element(XName.Get("plot-area", ChartNs));
        string? plotStyle = plot is null ? null : Attr(plot, ChartNs, "style-name");
        data.Type = kind switch
        {
            "chart:bar" => Property(plotStyle, "chart-properties", ChartNs, "vertical") == "true" ? "bar" : "column",
            "chart:line" => "line", "chart:area" => "area", "chart:circle" => "pie", "chart:ring" => "doughnut", "chart:scatter" => "scatter",
            "chart:radar" or "chart:filled-radar" => "radar", "chart:stock" => "stock", _ => ""
        };
        data.Filled = kind == "chart:filled-radar";
        if (data.Type.Length == 0 || plot is null) { data.Type = "column"; data.Notice = "This kind of chart is not shown in this version."; return data; }
        data.Stacked = Property(plotStyle, "chart-properties", ChartNs, "stacked") == "true" || Property(plotStyle, "chart-properties", ChartNs, "percentage") == "true";
        data.Percent = Property(plotStyle, "chart-properties", ChartNs, "percentage") == "true";

        // The cached table.
        var table = chart.Element(XName.Get("table", TableNs));
        var header = table?.Element(XName.Get("table-header-rows", TableNs))?.Element(XName.Get("table-row", TableNs));
        var names = header is null ? [] : Cells(header).Skip(1).Select(Text).ToList();
        var rows = table?.Element(XName.Get("table-rows", TableNs))?.Elements(XName.Get("table-row", TableNs)).Take(SheetDrawings.MaxPoints).Select(r => Cells(r).ToList()).ToList() ?? [];
        var series = plot.Elements(XName.Get("series", ChartNs)).Take(SheetDrawings.MaxSeries).ToList();
        if (series.Any(s => Attr(s, ChartNs, "class") is { } c && c != kind)) data.Notice = "Only the first part of this combined chart is shown.";
        var ofKind = series.Select((s, i) => (Series: s, Column: i + 1)).Where(p => (Attr(p.Series, ChartNs, "class") ?? kind) == kind).ToList();
        // The table keeps the layout of the data it came from: series in columns (categories in the first column), or,
        // when a series' own range is one row, series in rows (categories in the header row).
        bool byRow = series.Count > 0 && OneRow(Attr(series[0], ChartNs, "values-cell-range-address"));
        var headerCells = header is null ? [] : Cells(header).ToList();
        data.Categories = byRow ? headerCells.Skip(1).Select(Text).ToList() : rows.Select(r => r.Count > 0 ? Text(r[0]) : "").ToList();
        foreach (var (element, index) in ofKind)
        {
            string? style = Attr(element, ChartNs, "style-name");
            string? colour = data.Type is "line" or "scatter" ? Property(style, "graphic-properties", SvgNs, "stroke-color") : Property(style, "graphic-properties", DrawNs, "fill-color");
            var line = byRow && index - 1 < rows.Count ? rows[index - 1] : [];
            string name = byRow ? (line.Count > 0 ? Text(line[0]) : "") : index - 1 < names.Count ? names[index - 1] : "";
            var item = new ChartSeries
            {
                Name = name.Length > 0 ? name : $"Series {index}",
                Color = WorkbookStyles.Hex(colour?.TrimStart('#')),
                Values = byRow ? line.Skip(1).Select(Value).ToList() : rows.Select(r => index < r.Count ? Value(r[index]) : null).ToList()
            };
            if (data.Type == "scatter") item.X = byRow ? headerCells.Skip(1).Select(c => double.TryParse(Text(c), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : (double?)null).ToList()
                : rows.Select(r => r.Count > 0 ? Value(r[0]) : null).ToList();
            // Data labels: the series style's number part (value, percentage or both) and text part (the category).
            string? number = Property(style, "chart-properties", ChartNs, "data-label-number");
            item.PointLabels = SheetDrawings.PointLabels(data, item, number is "value" or "value-and-percentage", number is "percentage" or "value-and-percentage",
                Property(style, "chart-properties", ChartNs, "data-label-text") == "true", null);
            data.Series.Add(item);
        }
        // Axis titles: x is the category (scatter: X) axis, y the value axis.
        foreach (var axis in plot.Elements(XName.Get("axis", ChartNs)).Take(4))
        {
            string text = string.Join(" ", axis.Element(XName.Get("title", ChartNs))?.Elements().Select(p => p.Value.Trim()) ?? []).Trim();
            if (text.Length == 0) continue;
            string? dimension = Attr(axis, ChartNs, "dimension");
            if (dimension == "x" && data.CategoryTitle.Length == 0) data.CategoryTitle = text;
            else if (dimension == "y" && data.ValueTitle.Length == 0) data.ValueTitle = text;
        }
        if (data.Series.Count == 0) data.Notice = "This chart has no data to show.";
        return data;
    }

    // Whether a range such as "Sales.B2:Sales.C2" or "local-table.$B$2:.$C$2" spans several columns of one row.
    private static bool OneRow(string? range)
    {
        var ends = range?.Split(' ')[0].Split(':');
        if (ends is not { Length: 2 }) return false;
        (int Row, int Column)? Cell(string end) =>
            Spreadsheets.TryCell(end[(end.LastIndexOf('.') + 1)..].Replace("$", ""), out int row, out int column) ? (row, column) : null;
        return Cell(ends[0]) is { } a && Cell(ends[1]) is { } b && a.Row == b.Row && b.Column > a.Column;
    }

    // A row's cells, with repeated cells expanded (up to the series limit).
    private static IEnumerable<XElement> Cells(XElement row)
    {
        foreach (var cell in row.Elements().Where(e => e.Name.LocalName is "table-cell" or "covered-table-cell"))
        {
            int repeat = int.TryParse(Attr(cell, TableNs, "number-columns-repeated"), out int n) ? Math.Clamp(n, 1, SheetDrawings.MaxSeries + 1) : 1;
            for (int k = 0; k < repeat; k++) yield return cell;
        }
    }

    // A cell's paragraphs (not the range notes LibreOffice adds in draw:g).
    private static string Text(XElement cell) => string.Join("\n", cell.Elements(XName.Get("p", "urn:oasis:names:tc:opendocument:xmlns:text:1.0")).Select(p => p.Value)).Trim();

    private static double? Value(XElement cell) =>
        double.TryParse(Attr(cell, OfficeNs, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
}
