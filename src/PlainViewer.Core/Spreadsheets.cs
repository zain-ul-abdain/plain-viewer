using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ExcelNumberFormat;
namespace PlainViewer.Core;

// Reads .xlsx workbooks as display text. Only saved values are shown: formulas are never evaluated,
// external links and data connections are never followed, and hidden sheets stay hidden.
public static class Spreadsheets
{
    public const long SizeLimit = 256L * 1024 * 1024;
    public static readonly string[] Extensions = [".xlsx", ".xlsm", ".xltx", ".xltm"];
    public static bool IsWorkbook(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public const int MaxRowsPerSheet = 10_000, MaxColumns = 256, MaxCellsPerWorkbook = 300_000;
    public const string ResultUnavailable = "Result unavailable";
    private const long PartByteLimit = 1024L * 1024 * 1024;       // decompressed bytes read from any one part
    private const long StringCharacterLimit = 32L * 1024 * 1024;   // total shared-string characters kept
    private const string RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipNs = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private static readonly Dictionary<int, string> BuiltInFormats = new()
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%",
        [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)", [45] = "mm:ss",
        [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@"
    };

    // Excel's own row limit; sheets streamed to a row store may be this long.
    public const int MaxStoredRows = 1_048_576;

    // A saved number as Excel shows it, from its number format (built-in id or custom code). Shared by the .xlsx and
    // .xls readers. Built-in 14 and 22 follow the viewer's regional short date, as Excel does.
    internal static string FormatValue(double value, int id, IReadOnlyDictionary<int, string> custom, CultureInfo culture, bool date1904, Dictionary<string, NumberFormat> cache)
    {
        if (id is 14 or 22 && !custom.ContainsKey(id))
        {
            double serial = date1904 ? value + 1462 : value;
            var date = serial is >= -657435 and <= 2958465 ? DateTime.FromOADate(serial) : DateTime.MinValue;
            return id == 14 ? date.ToString(culture.DateTimeFormat.ShortDatePattern, culture) : date.ToString(culture.DateTimeFormat.ShortDatePattern + " H:mm", culture);
        }
        string code = custom.TryGetValue(id, out var own) ? own : BuiltInFormats.GetValueOrDefault(id, "General");
        if (!cache.TryGetValue(code, out var format)) cache[code] = format = new NumberFormat(code);
        try { return format.IsValid ? format.Format(value, culture, date1904) : value.ToString("G15", culture); }
        catch (Exception) { return value.ToString("G15", culture); }
    }

    // With storeFolder, sheets longer than MaxRowsPerSheet (or beyond the cell budget) stream to row stores there.
    public static DocumentView Load(string path, CultureInfo? culture = null, string? storeFolder = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        TextFiles.ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        // Templates and macro-enabled workbooks have the same parts as .xlsx; macros are never read, let alone run.
        if (extension is ".xlsb" or ".xlam")
            throw new DocumentException($"{extension} files are not supported yet. Save the workbook as .xlsx in a spreadsheet application to view it here.");
        if (!Extensions.Contains(extension)) throw new DocumentException("Only Excel workbooks (.xlsx, .xlsm, .xltx, .xltm) open in the spreadsheet view.");

        using var stream = LocalFiles.OpenRead(path);
        long length = stream.Length;
        var stamp = LocalFiles.Stamp(stream);
        if (length == 0) throw new DocumentException("This workbook is empty (0 bytes). It may not have finished downloading or copying. Get a complete copy and try again.");
        if (length > SizeLimit) throw new DocumentException("This workbook is larger than 256 MB, which is more than this viewer can open safely.");
        byte[] head = new byte[Math.Min(length, 65536)];
        stream.ReadExactly(head); stream.Position = 0;
        // Password protected: decrypted in memory with the password the user typed (OfficeEncryption).
        Stream package = stream;
        if (head.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
        {
            if (head.AsSpan().IndexOf(Encoding.Unicode.GetBytes("EncryptionInfo")) < 0)
                throw new DocumentException($"This looks like an older Excel file (.xls) saved with a {extension} name. Rename it to end in .xls to view it.");
            var encrypted = new byte[length]; stream.ReadExactly(encrypted); stream.Position = 0;
            try { package = new MemoryStream(OfficeEncryption.Decrypt(encrypted, "workbook"), false); }
            catch (InvalidDataException) { throw new DocumentException("This workbook is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
            head = new byte[8]; package.ReadExactly(head); package.Position = 0;
        }
        if (!head.AsSpan().StartsWith("PK\u0003\u0004"u8))
            throw new DocumentException($"This file is named {extension}, but its contents are not an Excel workbook. Open it with an application for its actual format.");

        DocumentView view;
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            // Sheet XML compresses well, so the total is generous; the ratio check still stops ZIP bombs.
            ArchiveSafety.Validate(zip, maximumBytes: 4L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
            view = new Reader(zip, culture, storeFolder).Read();
            if (zip.Entries.Any(e => OfficePackages.IsMacroPart(e.FullName)))
                view.Notice = ("This workbook contains macros. They were ignored and never ran. " + view.Notice).Trim();
        }
        catch (InvalidDataException) { throw Damaged(); }
        catch (XmlException) { throw Damaged(); }
        LocalFiles.ThrowIfChanged(stream, stamp);
        return view;
    }

    private static DocumentException Damaged() => new("This workbook is damaged or incomplete, so it cannot be shown. Try another copy of the file.");

    private sealed class Reader(ZipArchive zip, CultureInfo culture, string? storeFolder)
    {
        private readonly Dictionary<string, NumberFormat> formats = [];
        private WorkbookStyles styles = new();
        private List<string> strings = [];
        private bool date1904;
        private int cellBudget = MaxCellsPerWorkbook;
        private int formulasWithoutResult;
        private int conditionalNotShown;                      // conditional formatting rules that need formulas or today's date
        private bool conditionalOnLargeSheet;
        private bool truncated;

        public DocumentView Read()
        {
            string workbookPart = OfficeDocumentPart();
            var workbookRels = Relationships(workbookPart);
            var sheets = new List<(string Name, string State, string Id)>();
            using (var r = Open(workbookPart))
            {
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element) continue;
                    if (r.LocalName == "workbookPr") date1904 = r.GetAttribute("date1904") is "1" or "true";
                    else if (r.LocalName == "sheet")
                        sheets.Add((r.GetAttribute("name") ?? "Sheet", r.GetAttribute("state") ?? "visible",
                            r.GetAttribute("id", RelationshipNs) ?? r.GetAttribute("id", StrictRelationshipNs) ?? ""));
                }
            }
            string? stylesPart = null, themePart = null;
            foreach (var rel in workbookRels.Values)
            {
                if (rel.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)) strings = ReadSharedStrings(rel.Target);
                else if (rel.Type.EndsWith("/styles", StringComparison.Ordinal) && zip.GetEntry(rel.Target) is not null) stylesPart = rel.Target;
                else if (rel.Type.EndsWith("/theme", StringComparison.Ordinal) && zip.GetEntry(rel.Target) is not null) themePart = rel.Target;
            }
            styles = WorkbookStyles.Read(Open, stylesPart, themePart);

            var view = new DocumentView { Kind = "sheet", Encoding = "Excel workbook", CellStyles = styles.Table };
            var notes = new List<string>();
            int hidden = 0;
            foreach (var (name, state, id) in sheets)
            {
                if (state is "hidden" or "veryHidden") { hidden++; continue; }
                if (!workbookRels.TryGetValue(id, out var rel)) continue;
                if (rel.Type.EndsWith("/chartsheet", StringComparison.Ordinal)) { view.Sheets.Add(ReadChartSheet(name, rel.Target, view.Sheets.Count)); continue; }
                if (!rel.Type.EndsWith("/worksheet", StringComparison.Ordinal)) { notes.Add($"The sheet \"{name}\" is of a kind not shown in this version."); continue; }
                view.Sheets.Add(ReadSheet(name, rel.Target, view.Sheets.Count));
            }
            if (view.Sheets.Count == 0) throw new DocumentException("This workbook has no visible worksheets to show.");
            notes.AddRange(SheetDrawings.Notes(pictureBudget));
            if (ConditionalFormats.Note(conditionalNotShown) is { } conditionalNote) notes.Add(conditionalNote);
            if (conditionalOnLargeSheet) notes.Add(ConditionalFormats.LargeSheetNote);
            if (hidden > 0) notes.Add(hidden == 1 ? "1 hidden sheet stays hidden." : $"{hidden} hidden sheets stay hidden.");
            if (formulasWithoutResult > 0)
                notes.Add($"{formulasWithoutResult} formula cell{(formulasWithoutResult == 1 ? " has" : "s have")} no saved result and show{(formulasWithoutResult == 1 ? "s" : "")} \"{ResultUnavailable}\". Open the file in a spreadsheet application, recalculate and save it to see those values.");
            if (truncated) notes.Add(storeFolder is null
                ? $"Preview limit: only the first {MaxRowsPerSheet:N0} rows and {MaxColumns} columns of each sheet, up to {MaxCellsPerWorkbook:N0} cells in total, are shown."
                : $"Only the first {MaxColumns} columns and {MaxStoredRows:N0} rows of each sheet are shown.");
            view.Notice = string.Join(" ", notes);
            return view;
        }

        private string OfficeDocumentPart()
        {
            foreach (var rel in Relationships("").Values)
                if (rel.Type.EndsWith("/officeDocument", StringComparison.Ordinal)) return rel.Target;
            if (zip.GetEntry("xl/workbook.xml") is not null) return "xl/workbook.xml";
            throw new DocumentException("This file is not a valid Excel workbook, so it cannot be shown.");
        }

        private sealed record Relationship(string Type, string Target);

        // Relationships of a part. External targets (web addresses, other files) are skipped, never opened.
        private Dictionary<string, Relationship> Relationships(string part)
        {
            string directory = part.Contains('/') ? part[..(part.LastIndexOf('/') + 1)] : "";
            string relsPart = directory + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
            var result = new Dictionary<string, Relationship>();
            if (zip.GetEntry(relsPart) is null) return result;
            using var r = Open(relsPart);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                if (r.GetAttribute("TargetMode") == "External") continue;
                string? id = r.GetAttribute("Id"), type = r.GetAttribute("Type"), target = r.GetAttribute("Target");
                if (id is null || type is null || target is null) continue;
                result[id] = new Relationship(type, Resolve(directory, target));
            }
            return result;
        }

        private static string Resolve(string directory, string target)
        {
            var parts = new List<string>();
            string combined = target.StartsWith('/') ? target[1..] : directory + target;
            foreach (var piece in combined.Replace('\\', '/').Split('/'))
            {
                if (piece is "" or ".") continue;
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(piece);
            }
            return string.Join('/', parts);
        }

        private XmlReader Open(string part)
        {
            var entry = zip.GetEntry(part) ?? throw new DocumentException("This workbook is damaged or incomplete, so it cannot be shown. Try another copy of the file.");
            // Same protections as ArchiveSafety.CreateXmlReader (no DTDs, no resolver), with a byte cap instead
            // of a character cap because worksheets can be large.
            return XmlReader.Create(new LimitedStream(entry.Open(), PartByteLimit), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024,
                IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true
            });
        }

        private List<string> ReadSharedStrings(string part)
        {
            var list = new List<string>();
            if (zip.GetEntry(part) is null) return list;
            using var r = Open(part);
            StringBuilder? current = null; int phonetic = 0; long characters = 0;
            r.MoveToContent();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (r.LocalName == "t" && current is not null && phonetic == 0 && !r.IsEmptyElement)
                    {
                        string text = r.ReadElementContentAsString();
                        characters += text.Length;
                        if (characters > StringCharacterLimit) throw new DocumentException("This workbook contains more text than this viewer can show safely.");
                        current.Append(text);
                        continue;
                    }
                    if (r.LocalName == "si") { if (r.IsEmptyElement) list.Add(""); else current = new StringBuilder(); }
                    else if (r.LocalName == "rPh" && !r.IsEmptyElement) phonetic++;
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "si" && current is not null) { list.Add(current.ToString()); current = null; }
                    else if (r.LocalName == "rPh") phonetic--;
                }
                r.Read();
            }
            return list;
        }

        private readonly SheetDrawings.Budget pictureBudget = new();

        // The pictures and charts of a worksheet or chart sheet (its drawing part, if it has one).
        private List<SheetPicture> Drawing(string part, string drawingId, int index)
        {
            if (!Relationships(part).TryGetValue(drawingId, out var rel)) return [];
            return SheetDrawings.Read(rel.Target, p => Relationships(p).ToDictionary(pair => pair.Key, pair => (pair.Value.Type, pair.Value.Target)),
                Open, zip.GetEntry, storeFolder, index, pictureBudget, styles.Theme);
        }

        // A chart sheet: no cells, its chart shown filling the view.
        private SheetData ReadChartSheet(string name, string part, int index)
        {
            var sheet = new SheetData { Name = name, ChartSheet = true };
            string? drawingId = null;
            using (var r = Open(part))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "drawing")
                        drawingId = r.GetAttribute("id", RelationshipNs) ?? r.GetAttribute("id", StrictRelationshipNs);
            if (drawingId is not null) sheet.Pictures = Drawing(part, drawingId, index).Where(p => p.Chart is not null).Take(1).ToList();
            if (sheet.Pictures.Count == 0) sheet.Notice = "This chart sheet has no chart to show.";
            return sheet;
        }

        private SheetData ReadSheet(string name, string part, int index)
        {
            var sheet = new SheetData { Name = name };
            var rows = new SortedDictionary<int, List<(int Column, string Text, char Align, int Style)>>();
            var widths = new List<(int Min, int Max, double Width, bool Hidden)>();
            // Formatting of whole columns and rows whose fill or borders show (style numbers into the style table).
            var columnStyles = new List<(int Min, int Max, int Look)>();
            var rowStyles = new SortedDictionary<int, int>();
            double defaultWidth = 8.43;
            int maxRow = 0, maxColumn = 0, rowNumber = 0, stored = 0;
            bool firstView = true, stopped = false;
            string? drawingId = null;
            // Saved numbers and errors of cells held in memory, for conditional formatting (applied after the sheet).
            var numbers = new Dictionary<long, double>(); var errors = new HashSet<long>();
            var conditional = new ConditionalFormats(styles);
            // A long sheet switches to streaming its rows (in order) into a row store; rows read so far go first.
            RowStoreWriter? store = null;
            try
            {
            using var r = Open(part);
            r.MoveToContent();
            while (!r.EOF)
            {
                if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }
                switch (r.LocalName)
                {
                    case "sheetView":
                        if (firstView) sheet.RightToLeft = r.GetAttribute("rightToLeft") is "1" or "true";
                        firstView = false; break;
                    case "pane":
                        if (r.GetAttribute("state") is "frozen" or "frozenSplit")
                        {
                            sheet.FrozenColumns = (int)Math.Min(MaxColumns, ParseDouble(r.GetAttribute("xSplit")));
                            sheet.FrozenRows = (int)Math.Min(MaxRowsPerSheet, ParseDouble(r.GetAttribute("ySplit")));
                        }
                        break;
                    case "sheetFormatPr":
                        if (r.GetAttribute("defaultColWidth") is { } dw) defaultWidth = ParseDouble(dw);
                        else if (r.GetAttribute("baseColWidth") is { } bw) defaultWidth = ParseDouble(bw) + 0.71;
                        if (r.GetAttribute("defaultRowHeight") is { } dh && ParseDouble(dh) is > 0 and < 410 and var height) sheet.DefaultRowHeight = height;
                        break;
                    case "col":
                        if (int.TryParse(r.GetAttribute("min"), out int min) && int.TryParse(r.GetAttribute("max"), out int max))
                        {
                            widths.Add((min, Math.Min(max, MaxColumns), r.GetAttribute("width") is { } w ? ParseDouble(w) : -1, r.GetAttribute("hidden") is "1" or "true"));
                            if (VisibleStyle(r.GetAttribute("style")) is int columnLook && columnStyles.Count < 1000) columnStyles.Add((min, max, columnLook));
                        }
                        break;
                    case "row":
                        rowNumber = int.TryParse(r.GetAttribute("r"), out int rn) ? rn : rowNumber + 1;
                        if (store is null && !stopped && storeFolder is not null && (rowNumber > MaxRowsPerSheet || cellBudget <= 0))
                            store = StartStore(index, rows, ref stored);
                        if (stopped || (store is null ? rowNumber > MaxRowsPerSheet || cellBudget <= 0 : rowNumber > MaxStoredRows))
                        {
                            truncated = true; stopped = true;
                            r.Skip(); continue;                       // keep scanning for merges after the data
                        }
                        if (store is not null && rowNumber <= stored)
                            throw new DocumentException("This workbook lists the rows of a large sheet out of order, which this viewer cannot show.");
                        if (r.GetAttribute("hidden") is "1" or "true") sheet.HiddenRows.Add(rowNumber);
                        // The saved height (Excel also saves the height it fitted to wrapped text or larger fonts).
                        if (r.GetAttribute("ht") is { } ht) RowHeight(sheet, rowNumber, ParseDouble(ht));
                        if (store is null && r.GetAttribute("customFormat") is "1" or "true" && VisibleStyle(r.GetAttribute("s")) is int rowLook) rowStyles[rowNumber] = rowLook;
                        if (r.IsEmptyElement) break;
                        var cells = ReadRow(r, rowNumber, ref maxColumn, limited: store is null, store is null ? numbers : null, errors);
                        if (store is not null) Store(store, ref stored, rowNumber, cells);
                        else if (cells.Count > 0) { rows[rowNumber] = cells; maxRow = Math.Max(maxRow, rowNumber); }
                        continue;                                    // ReadRow leaves the reader after </row>
                    case "mergeCell":
                        if (r.GetAttribute("ref") is { } reference && TryRange(reference, out var range)) sheet.Merges.Add(range);
                        break;
                    case "drawing":
                        drawingId = r.GetAttribute("id", RelationshipNs) ?? r.GetAttribute("id", StrictRelationshipNs);
                        break;
                    case "conditionalFormatting" when store is not null: conditionalOnLargeSheet = true; break;
                    case "conditionalFormatting":
                        // A subtree reader leaves the reader on the element's end; the loop's Read moves past it.
                        using (var subtree = r.ReadSubtree()) conditional.Read(System.Xml.Linq.XElement.Load(subtree));
                        break;
                }
                r.Read();
            }
            if (store is not null)
            {
                store.Complete();
                sheet.Store = $"sheet{index}";
                sheet.FrozenRows = Math.Min(sheet.FrozenRows, maxRow);   // the header rows come from the first rows
            }
            }
            finally { store?.Dispose(); }
            if (drawingId is not null) sheet.Pictures = Drawing(part, drawingId, index);
            if (sheet.Store.Length == 0 && (columnStyles.Count > 0 || rowStyles.Count > 0)) DefaultStyles(rows, columnStyles, rowStyles, ref maxRow, ref maxColumn);
            if (conditional.Any && sheet.Store.Length == 0) conditional.Apply(rows, numbers, errors, maxColumn);
            conditionalNotShown += conditional.NotShown;

            int totalRows = sheet.Store.Length > 0 ? stored : 0;
            maxRow = Math.Max(maxRow, sheet.FrozenRows);
            maxColumn = Math.Max(maxColumn, sheet.FrozenColumns);
            foreach (var (row, cells) in rows)
            {
                var text = Empty(maxColumn);
                while (sheet.Rows.Count < row - 1) { sheet.Rows.Add(Empty(maxColumn)); sheet.Align.Add(new string('l', maxColumn)); }
                foreach (var (column, value, _, _) in cells) text[column] = value;
                sheet.Rows.Add(text); sheet.Align.Add(Layout(cells, maxColumn));
            }
            while (sheet.Rows.Count < maxRow) { sheet.Rows.Add(Empty(maxColumn)); sheet.Align.Add(new string('l', maxColumn)); }
            for (int c = 1; c <= maxColumn; c++)
            {
                double width = defaultWidth;
                foreach (var span in widths)
                    if (c >= span.Min && c <= span.Max) width = span.Hidden ? 0 : span.Width >= 0 ? span.Width : width;
                sheet.ColumnWidths.Add(Math.Round(width, 2));
            }
            totalRows = Math.Max(totalRows, maxRow);
            sheet.RowCount = sheet.Store.Length > 0 ? stored : sheet.Rows.Count;
            sheet.Merges = sheet.Merges.Where(m => m[0] < totalRows && m[1] < maxColumn)
                .Select(m => new[] { m[0], m[1], Math.Min(m[2], totalRows - 1), Math.Min(m[3], maxColumn - 1) }).ToList();
            if (sheet.RowCount == 0) sheet.Notice = "This sheet is empty.";
            return sheet;
        }

        private static string[] Empty(int count) { var row = new string[count]; Array.Fill(row, ""); return row; }

        // A column's or row's style number in the style table when its fill or borders show; otherwise null.
        private int? VisibleStyle(string? attribute) =>
            int.TryParse(attribute, out int s) && s >= 0 && s < styles.StyleIds.Count && styles.Table[styles.StyleIds[s]].Visible ? styles.StyleIds[s] : null;

        // Whole formatted columns and rows: Excel saves no cells for their empty part, so the empty cells are added with
        // the row's style (it wins, as in Excel) or the column's, within the sheet's data. A column formatted on its own
        // widens the grid to reach it; formatting that runs to Excel's last column (the whole sheet) stays within the data.
        private void DefaultStyles(SortedDictionary<int, List<(int Column, string Text, char Align, int Style)>> rows,
            List<(int Min, int Max, int Look)> columnStyles, SortedDictionary<int, int> rowStyles, ref int maxRow, ref int maxColumn)
        {
            foreach (var (_, max, _) in columnStyles) if (max <= MaxColumns) maxColumn = Math.Max(maxColumn, max);
            if (rowStyles.Count > 0) maxRow = Math.Max(maxRow, Math.Min(MaxRowsPerSheet, rowStyles.Keys.Max()));
            var byColumn = new int[maxColumn];
            foreach (var (min, max, look) in columnStyles)
                for (int c = Math.Max(1, min); c <= Math.Min(max, maxColumn); c++) byColumn[c - 1] = look;
            for (int row = 1; row <= maxRow; row++)
            {
                int rowLook = rowStyles.GetValueOrDefault(row);
                rows.TryGetValue(row, out var list);
                var present = list?.Select(cell => cell.Column).ToHashSet();
                for (int c = 0; c < maxColumn; c++)
                {
                    int look = rowLook != 0 ? rowLook : byColumn[c];
                    if (look == 0 || present?.Contains(c) == true) continue;
                    if (cellBudget-- <= 0) return;
                    if (list is null) rows[row] = list = [];
                    list.Add((c, "", 'l', look));
                }
            }
        }

        // A row's alignment characters, then "|" and its cells' style numbers if any cell has a style (see SheetData.Align).
        private static string Layout(List<(int Column, string Text, char Align, int Style)> cells, int width)
        {
            var align = new char[width]; Array.Fill(align, 'l');
            var style = new int[width];
            foreach (var (column, _, a, s) in cells) { align[column] = a; style[column] = s; }
            return style.Any(s => s != 0) ? new string(align) + "|" + string.Join('.', style) : new string(align);
        }

        private RowStoreWriter StartStore(int index, SortedDictionary<int, List<(int Column, string Text, char Align, int Style)>> rows, ref int stored)
        {
            var store = new RowStoreWriter(storeFolder!, $"sheet{index}");
            try { foreach (var (row, cells) in rows) Store(store, ref stored, row, cells); }
            catch { store.Dispose(); throw; }
            return store;
        }

        private static readonly string[] EmptyStoredRow = [""];

        // Appends Excel row `row` (after empty rows for any gap) as [alignment and styles, cell, cell, ...].
        private static void Store(RowStoreWriter store, ref int stored, int row, List<(int Column, string Text, char Align, int Style)> cells)
        {
            while (stored < row - 1) { store.Add(EmptyStoredRow); stored++; }
            int width = cells.Count == 0 ? 0 : cells.Max(cell => cell.Column) + 1;
            var fields = new string[width + 1];
            Array.Fill(fields, "");
            foreach (var (column, text, _, _) in cells) fields[column + 1] = text;
            fields[0] = Layout(cells, width);
            store.Add(fields); stored++;
        }

        // Reads one <row>; returns its cells and leaves the reader positioned after </row>. `limited`: count cells
        // against the workbook's in-memory budget (rows streamed to a row store are not limited by it).
        private List<(int, string, char, int)> ReadRow(XmlReader r, int rowNumber, ref int maxColumn, bool limited,
            Dictionary<long, double>? numbers = null, HashSet<long>? errors = null)
        {
            var cells = new List<(int, string, char, int)>();
            int column = -1;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.LocalName == "row"))
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                {
                    column = r.GetAttribute("r") is { } reference && TryCell(reference, out _, out int parsed) ? parsed : column + 1;
                    string? type = r.GetAttribute("t");
                    int style = int.TryParse(r.GetAttribute("s"), out int s) ? s : 0;
                    string? value = null, inline = null; bool formula = false;
                    if (!r.IsEmptyElement)
                    {
                        int depth = r.Depth; r.Read();
                        while (!r.EOF && r.Depth > depth)
                        {
                            if (r.NodeType == XmlNodeType.Element && r.LocalName == "f") { formula = true; r.Skip(); continue; }
                            if (r.NodeType == XmlNodeType.Element && r.LocalName == "v") { value = r.ReadElementContentAsString(); continue; }
                            if (r.NodeType == XmlNodeType.Element && r.LocalName == "t") { inline = (inline ?? "") + r.ReadElementContentAsString(); continue; }
                            r.Read();
                        }
                    }
                    if (column < MaxColumns && (!limited || cellBudget > 0))
                    {
                        string text = Display(type, value, inline, formula, style, out char align);
                        // The saved value itself, for conditional formatting.
                        if (numbers is not null && numbers.Count < MaxCellsPerWorkbook)
                        {
                            long key = ConditionalFormats.Key(rowNumber - 1, column);
                            if (type == "e") errors?.Add(key);
                            else if (type is null or "n" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double saved) && double.IsFinite(saved)) numbers[key] = saved;
                            else if (type == "d" && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) numbers[key] = date.ToOADate() - (date1904 ? 1462 : 0);
                        }
                        bool known = style >= 0 && style < styles.StyleIds.Count;
                        int look = known ? styles.StyleIds[style] : 0;
                        if (known && styles.Horizontal[style] != '\0') align = styles.Horizontal[style];
                        // Empty cells matter only when their fill or border shows.
                        if (text.Length > 0 || styles.Table[look].Visible)
                        { cells.Add((column, text, align, look)); maxColumn = Math.Max(maxColumn, column + 1); if (limited) cellBudget--; }
                    }
                    else truncated = true;
                }
                r.Read();
            }
            r.Read();
            return cells;
        }

        private string Display(string? type, string? value, string? inline, bool formula, int style, out char align)
        {
            align = 'l';
            if (formula && value is null && inline is null) { formulasWithoutResult++; return ResultUnavailable; }
            switch (type)
            {
                case "s": return int.TryParse(value, out int index) && index >= 0 && index < strings.Count ? strings[index] : "";
                case "inlineStr": return inline ?? "";
                case "str": return value ?? "";
                case "b": align = 'c'; return value is "1" or "true" ? "TRUE" : "FALSE";
                case "e": align = 'c'; return value ?? "";
                case "d":
                    align = 'r';
                    if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)) return value ?? "";
                    return FormatNumber(date.ToOADate() - (date1904 ? 1462 : 0), style);
                default:
                    if (value is null) return "";
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return value;
                    align = 'r';
                    return FormatNumber(number, style);
            }
        }

        private string FormatNumber(double value, int style)
        {
            int id = style >= 0 && style < styles.NumberFormats.Count ? styles.NumberFormats[style] : 0;
            return FormatValue(value, id, styles.CustomFormats, culture, date1904, formats);
        }

        private DateTime FromSerial(double value)
        {
            double serial = date1904 ? value + 1462 : value;
            return serial is >= -657435 and <= 2958465 ? DateTime.FromOADate(serial) : DateTime.MinValue;
        }
    }

    private static double ParseDouble(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    // Records a row's height in points when it differs from the sheet's default (Excel allows up to 409 points).
    internal static void RowHeight(SheetData sheet, int row, double points)
    {
        if (!double.IsFinite(points) || points < 0 || points > 409 || Math.Abs(points - sheet.DefaultRowHeight) < 0.1) return;
        if (sheet.RowHeights.Count < 100_000) sheet.RowHeights[row] = Math.Round(points, 2);
    }

    public static bool TryCell(string reference, out int row, out int column)
    {
        row = 0; column = 0; int i = 0, letters = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i])) { column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1); i++; letters++; }
        if (letters is 0 or > 3 || !int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row) || row < 1) return false;
        column--; return true;
    }

    public static bool TryRange(string reference, out int[] range)
    {
        range = [];
        var parts = reference.Split(':');
        if (parts.Length != 2 || !TryCell(parts[0], out int r1, out int c1) || !TryCell(parts[1], out int r2, out int c2)) return false;
        range = [Math.Min(r1, r2) - 1, Math.Min(c1, c2), Math.Max(r1, r2) - 1, Math.Max(c1, c2)];
        return true;
    }

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            read += n;
            if (read > limit) throw new DocumentException("This file is damaged or too large to open safely. Try another copy of the file.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
