using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ExcelNumberFormat;
namespace PlainViewer.Core;

// Reads Excel 97-2003 (.xls) and OpenDocument (.ods) spreadsheets as display text, like the .xlsx reader: only saved
// values are shown and formulas are never evaluated. (Converting these files with LibreOffice recalculated them, which
// the specification forbids: tested with an .xls whose saved result had been changed.) Nothing a file refers to is
// opened. Cell formatting (fonts, fills, borders, alignment) becomes CellStyles like the .xlsx reader's. The first
// 10,000 rows of a sheet stay in memory; with a store folder, longer sheets stream every row to a row store there,
// as for .xlsx.
public static partial class LegacySpreadsheets
{
    // .xlt and .ots are templates (0.8.0), shown as the workbook they hold; .fods is the single-XML-file form of .ods.
    public static readonly string[] Extensions = [".xls", ".xlt", ".ods", ".ots", ".fods"];
    public static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static DocumentView Load(string path, CultureInfo? culture = null, string? storeFolder = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        TextFiles.ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Handles(path)) throw new DocumentException($"{extension} files do not open in this view.");
        byte[] bytes;
        using (var stream = LocalFiles.OpenRead(path))
        {
            long length = stream.Length;
            var stamp = LocalFiles.Stamp(stream);
            if (length == 0) throw new DocumentException("This spreadsheet is empty (0 bytes). It may not have finished downloading or copying. Get a complete copy and try again.");
            if (length > Spreadsheets.SizeLimit) throw new DocumentException("This spreadsheet is larger than 256 MB, which is more than this viewer can open safely.");
            bytes = new byte[length];
            stream.ReadExactly(bytes);
            LocalFiles.ThrowIfChanged(stream, stamp);
        }
        ReadOnlySpan<byte> head = bytes;
        string Named() => $"This file is named {extension}, but its contents are not a spreadsheet this view can show. Open it with an application for its actual format.";
        try
        {
            if (CompoundFile.IsCompoundFile(head))
            {
                var file = new CompoundFile(bytes, "Excel 97–2003 workbook");
                if (file.Has("EncryptionInfo") || file.Has("EncryptedPackage"))
                    throw new DocumentException("This workbook is protected with a password. Password-protected workbooks cannot be opened in this version. Remove the password in Excel, or ask the sender for an unprotected copy.");
                if (!file.Has("Workbook") && !file.Has("Book")) throw new DocumentException(Named());
                if (file.Find("Workbook") is null) throw new DocumentException("This is an Excel 5.0 or Excel 95 workbook, which is older than this viewer supports. Save it in a newer format to view it.");
                return new Excel97(file, culture, storeFolder).Read();
            }
            if (head.StartsWith("PK\u0003\u0004"u8))
            {
                using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
                if (zip.GetEntry("[Content_Types].xml") is not null)
                    throw new DocumentException($"This is a newer Excel file (.xlsx) saved with a {extension} name. Rename it to end in .xlsx to view it.");
                string mime = "";
                if (zip.GetEntry("mimetype") is { Length: < 200 } entry) using (var reader = new StreamReader(entry.Open())) mime = reader.ReadToEnd().Trim();
                if (mime is not ("application/vnd.oasis.opendocument.spreadsheet" or "application/vnd.oasis.opendocument.spreadsheet-template")) throw new DocumentException(Named());
                // Password protected: decrypted in memory with the password the user typed (OpenDocumentEncryption).
                ArchiveSafety.Validate(zip, maximumBytes: 2L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
                if (OpenDocumentEncryption.IsEncrypted(zip))
                {
                    using var plain = new ZipArchive(new MemoryStream(OpenDocumentEncryption.Decrypt(zip, "spreadsheet"), false), ZipArchiveMode.Read);
                    return new OpenDocumentSheets(plain, storeFolder).Read();
                }
                return new OpenDocumentSheets(zip, storeFolder).Read();
            }
            if (FlatOpenDocument(bytes) is { } flat)
            {
                using var zip = new ZipArchive(new MemoryStream(flat, false), ZipArchiveMode.Read);
                var view = new OpenDocumentSheets(zip, storeFolder).Read();
                view.Encoding = "OpenDocument spreadsheet (flat XML)";
                return view;
            }
        }
        catch (InvalidDataException) { throw Damaged(); }
        catch (XmlException) { throw Damaged(); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { throw Damaged(); }
        throw new DocumentException(Named());
    }

    public const long FlatSizeLimit = 64L * 1024 * 1024;

    // A flat OpenDocument spreadsheet (.fods): one XML file holding what an .ods keeps in several parts. It is read
    // through the .ods reader as a package whose content.xml is the whole file (the reader takes styles from it as it
    // goes) plus a settings.xml copied from its office:settings, for frozen panes. Pictures and charts in a flat file
    // are stored inside the XML rather than as parts, so they are not shown. Null when the bytes are not one.
    private static byte[]? FlatOpenDocument(byte[] bytes)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024, IgnoreComments = true, IgnoreProcessingInstructions = true };
        const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        int start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf]) ? 3 : 0;
        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        if (start >= bytes.Length || bytes[start] != (byte)'<') return null;
        string? settingsXml = null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes, false), settings);
            reader.MoveToContent();
            if (reader.LocalName != "document" || reader.NamespaceURI != OfficeNs) return null;
            if (reader.GetAttribute("mimetype", OfficeNs) is not ("application/vnd.oasis.opendocument.spreadsheet" or "application/vnd.oasis.opendocument.spreadsheet-template"))
                throw new DocumentException("This OpenDocument file is not a spreadsheet. Open it with an application for its actual format.");
            if (bytes.Length > FlatSizeLimit) throw new DocumentException("This flat OpenDocument spreadsheet is larger than 64 MB, which is more than this viewer can open safely. Save it as .ods to view it.");
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != OfficeNs) continue;
                if (reader.LocalName == "settings") { settingsXml = reader.ReadOuterXml(); break; }
                if (reader.LocalName == "body") break;
            }
        }
        catch (XmlException) { return null; }
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()) mimetype.Write("application/vnd.oasis.opendocument.spreadsheet"u8);
            using (var content = zip.CreateEntry("content.xml", CompressionLevel.NoCompression).Open()) content.Write(bytes);
            if (settingsXml is not null) using (var part = zip.CreateEntry("settings.xml", CompressionLevel.Fastest).Open()) part.Write(Encoding.UTF8.GetBytes(settingsXml));
        }
        return output.ToArray();
    }

    private static DocumentException Damaged() => new("This spreadsheet is damaged or incomplete, so it cannot be shown. Try another copy of the file.");

    // Distinct cell styles of a workbook; entry 0 is the plain default.
    private sealed class StyleTable
    {
        public readonly List<CellStyle> Table = [new()];
        private readonly Dictionary<string, int> known = new() { [JsonSerializer.Serialize(new CellStyle())] = 0 };
        public int Add(CellStyle style)
        {
            string key = JsonSerializer.Serialize(style);
            if (!known.TryGetValue(key, out int index)) { index = known[key] = Table.Count; Table.Add(style); }
            return index;
        }

        // For styles made by conditional formatting, which can differ in every cell: -1 once the table is full.
        public int TryAdd(CellStyle style) => Table.Count < WorkbookStyles.MaxStyles || known.ContainsKey(JsonSerializer.Serialize(style)) ? Add(style) : -1;
    }

    // One sheet's cells. The first rows are kept in memory for the first screen; with a store folder, a sheet longer
    // than that (or beyond the workbook's in-memory cell budget) streams every row, in order, to a row store.
    private sealed class SheetBuilder(string name, int index, string? storeFolder, StyleTable styles) : IDisposable
    {
        public readonly SheetData Sheet = new() { Name = name };
        public readonly Dictionary<int, double> Widths = [];          // column -> Excel character width (0 = hidden)
        public double DefaultWidth = 8.43;
        public bool Truncated;
        private readonly SortedDictionary<int, SortedDictionary<int, (string Text, char Align, int Style)>> memory = [];
        private SortedDictionary<int, (string Text, char Align, int Style)> pending = [];
        private RowStoreWriter? store;
        private int stored, pendingRow = -1, columns;
        // Formatting of whole columns and rows whose fill or borders show (zero-based; style numbers into the table).
        private readonly List<(int First, int Last, int Style)> columnStyles = [];
        private readonly SortedDictionary<int, int> rowStyles = [];
        public void ColumnStyle(int first, int last, int style) { if (style > 0 && styles.Table[style].Visible && columnStyles.Count < 1000) columnStyles.Add((first, last, style)); }
        public void RowStyle(int row, int style) { if (style > 0 && styles.Table[style].Visible && row < Spreadsheets.MaxRowsPerSheet && rowStyles.Count < 100_000) rowStyles[row] = style; }

        // The empty cells of formatted columns and rows, which the file does not list: the row's style wins, as in
        // Excel. A column formatted on its own widens the sheet to reach it; formatting that runs to the format's last
        // column (the whole sheet) stays within the data. Rows held in memory only.
        public void ApplyDefaultStyles(int lastColumnOfFormat, ref int budget)
        {
            if (store is not null || columnStyles.Count == 0 && rowStyles.Count == 0) return;
            foreach (var (_, last, _) in columnStyles) if (last < lastColumnOfFormat) columns = Math.Max(columns, Math.Min(last + 1, Spreadsheets.MaxColumns));
            int lastRow = Math.Max(memory.Count == 0 ? -1 : memory.Keys.Max(), rowStyles.Count == 0 ? -1 : rowStyles.Keys.Max());
            var byColumn = new int[columns];
            foreach (var (first, last, style) in columnStyles)
                for (int c = Math.Max(0, first); c <= Math.Min(last, columns - 1); c++) byColumn[c] = style;
            for (int r = 0; r <= lastRow; r++)
            {
                int rowStyle = rowStyles.GetValueOrDefault(r);
                memory.TryGetValue(r, out var line);
                for (int c = 0; c < columns; c++)
                {
                    int style = rowStyle != 0 ? rowStyle : byColumn[c];
                    if (style == 0 || line?.ContainsKey(c) == true) continue;
                    if (budget-- <= 0) return;
                    if (line is null) memory[r] = line = [];
                    line[c] = ("", 'l', style);
                }
            }
        }

        // Saved numbers and error cells of the rows held in memory, for conditional formatting.
        private readonly Dictionary<long, double> numbers = [];
        private readonly HashSet<long> errors = [];

        public void Value(int row, int column, double? number, bool error)
        {
            if (store is not null || row >= Spreadsheets.MaxRowsPerSheet || column >= Spreadsheets.MaxColumns || numbers.Count + errors.Count >= Spreadsheets.MaxCellsPerWorkbook) return;
            if (number is double v && double.IsFinite(v)) numbers[ConditionalFormats.Key(row, column)] = v;
            if (error) errors.Add(ConditionalFormats.Key(row, column));
        }

        // Applies conditional formatting to the rows held in memory; false when the sheet streams to a row store (not
        // applied there).
        public bool ApplyConditional(ConditionalFormats formats)
        {
            if (!formats.Any) return true;
            if (store is not null) return false;
            var cells = new Dictionary<long, (string Text, CellStyle Style)>();
            int maxRow = -1;
            foreach (var (r, line) in memory)
            {
                foreach (var (c, cell) in line) cells[ConditionalFormats.Key(r, c)] = (cell.Text, styles.Table[cell.Style]);
                maxRow = Math.Max(maxRow, r);
            }
            foreach (var (row, column, style, hideText) in formats.Evaluate(cells, numbers, errors, maxRow, columns))
            {
                int index = styles.TryAdd(style);
                if (index < 0) continue;
                if (!memory.TryGetValue(row, out var line)) memory[row] = line = [];
                line[column] = line.TryGetValue(column, out var cell) ? (hideText ? "" : cell.Text, cell.Align, index) : ("", 'l', index);
            }
            return true;
        }

        public void Add(int row, int column, string text, char align, int style, ref int budget)
        {
            if (column >= Spreadsheets.MaxColumns) { Truncated = true; return; }
            if (text.Length == 0 && !styles.Table[style].Visible) return;
            if (store is null)
            {
                bool full = row >= Spreadsheets.MaxRowsPerSheet || (budget <= 0 && !(memory.TryGetValue(row, out var r) && r.ContainsKey(column)));
                if (!full)
                {
                    if (!memory.TryGetValue(row, out var line)) memory[row] = line = [];
                    if (!line.ContainsKey(column)) budget--;
                    line[column] = (text, align, style); columns = Math.Max(columns, column + 1);
                    return;
                }
                if (storeFolder is null) { Truncated = true; return; }
                StartStore();
            }
            if (row >= Spreadsheets.MaxStoredRows) { Truncated = true; return; }
            if (row < pendingRow || row < stored)
                throw new DocumentException("This workbook lists the rows of a large sheet out of order, which this viewer cannot show.");
            if (row > pendingRow) { Flush(); pendingRow = row; }
            pending[column] = (text, align, style); columns = Math.Max(columns, column + 1);
        }

        // Writes the rows read so far; the last one (it may be incomplete when the cell budget ran out within it)
        // becomes the row being streamed, and leaves the in-memory first screen.
        private void StartStore()
        {
            store = new RowStoreWriter(storeFolder!, $"sheet{index}");
            if (memory.Count == 0) return;
            int last = memory.Keys.Max();
            for (int r = 0; r < last; r++) Write(r, memory.GetValueOrDefault(r) ?? []);
            pending = memory[last]; pendingRow = last;
            memory.Remove(last);
        }

        private void Flush()
        {
            if (pendingRow < 0) return;
            Write(pendingRow, pending);
            pending = []; pendingRow = -1;
        }

        // Row `row` as [layout, cell, cell, ...], after empty rows for any gap.
        private void Write(int row, SortedDictionary<int, (string Text, char Align, int Style)> cells)
        {
            while (stored < row) { store!.Add([""]); stored++; }
            int width = cells.Count == 0 ? 0 : cells.Keys.Max() + 1;
            var fields = new string[width + 1];
            Array.Fill(fields, "");
            foreach (var (c, cell) in cells) fields[c + 1] = cell.Text;
            fields[0] = Layout(cells, width);
            store!.Add(fields); stored++;
        }

        // Alignment characters, then "|" and a style number per cell if any cell has one (see SheetData.Align).
        private static string Layout(SortedDictionary<int, (string Text, char Align, int Style)> cells, int width)
        {
            var align = new char[width]; var style = new int[width];
            Array.Fill(align, 'l');
            foreach (var (c, cell) in cells) if (c < width) { align[c] = cell.Align; style[c] = cell.Style; }
            return style.Any(s => s != 0) ? new string(align) + "|" + string.Join('.', style) : new string(align);
        }

        public SheetData Build()
        {
            if (store is not null) { Flush(); store.Complete(); Sheet.Store = $"sheet{index}"; }
            int width = Math.Max(Sheet.FrozenColumns, columns);
            int inMemory = memory.Count == 0 ? 0 : memory.Keys.Max() + 1;
            int rows = Math.Max(Sheet.FrozenRows, store is not null ? stored : inMemory);
            // The first screen: every row of a sheet that did not need a store; otherwise only the rows complete in memory
            // (the page reads every later row from the store).
            int firstScreen = store is not null ? inMemory : rows;
            for (int r = 0; r < firstScreen; r++)
            {
                var cells = memory.GetValueOrDefault(r) ?? [];
                var text = new string[width]; Array.Fill(text, "");
                foreach (var (c, cell) in cells) if (c < width) text[c] = cell.Text;
                Sheet.Rows.Add(text); Sheet.Align.Add(Layout(cells, width));
            }
            for (int c = 0; c < width; c++) Sheet.ColumnWidths.Add(Math.Round(Widths.TryGetValue(c, out double w) ? w : DefaultWidth, 2));
            Sheet.RowCount = rows;
            if (store is not null) Sheet.FrozenRows = Math.Min(Sheet.FrozenRows, Sheet.Rows.Count);
            Sheet.HiddenRows = Sheet.HiddenRows.Where(r => r <= rows).ToList();
            Sheet.Merges = Sheet.Merges.Where(m => m[0] < rows && m[1] < width)
                .Select(m => new[] { m[0], m[1], Math.Min(m[2], rows - 1), Math.Min(m[3], width - 1) }).ToList();
            if (rows == 0) Sheet.Notice = "This sheet is empty.";
            return Sheet;
        }

        public void Dispose() => store?.Dispose();
    }

    private static string Notes(int hidden, bool truncated, bool stored, List<string> extra)
    {
        var notes = new List<string>(extra);
        if (hidden > 0) notes.Add(hidden == 1 ? "1 hidden sheet stays hidden." : $"{hidden} hidden sheets stay hidden.");
        if (truncated) notes.Add(stored
            ? $"Only the first {Spreadsheets.MaxColumns} columns and {Spreadsheets.MaxStoredRows:N0} rows of each sheet are shown."
            : $"Preview limit: only the first {Spreadsheets.MaxRowsPerSheet:N0} rows and {Spreadsheets.MaxColumns} columns of each sheet, up to {Spreadsheets.MaxCellsPerWorkbook:N0} cells in total, are shown.");
        return string.Join(" ", notes);
    }

    // ---- Excel 97-2003 (BIFF8) ----

    private sealed partial class Excel97(CompoundFile file, CultureInfo culture, string? storeFolder)
    {
        private const string Label = "Excel 97–2003 workbook";
        private byte[] data = [];
        private readonly List<string> strings = [];
        private readonly Dictionary<int, string> custom = [];
        private readonly List<int> formats = [];             // per XF: number format id
        private readonly List<char> horizontal = [];         // per XF: l, r, c or '\0' (general)
        private readonly List<int> xfStyles = [];            // per XF: index into the style table
        private readonly List<(bool Bold, bool Italic, bool Underline, bool Strike, int Height, int Colour, string Name)> fonts = [];
        private readonly string[] palette = (string[])WorkbookStyles.Palette.Clone();
        private readonly List<(int Offset, int Length)> xfRecords = [];
        private readonly StyleTable styles = new();
        private readonly Dictionary<string, NumberFormat> cache = [];
        private bool date1904, truncated, stored, conditionalOnLargeSheet;
        private int budget = Spreadsheets.MaxCellsPerWorkbook, conditionalNotShown;

        private readonly record struct Record(int Type, int Offset, int Length);

        // The records of the substream starting at `from`, including any substream nested in it (an embedded chart has
        // its own BOF and EOF), up to its own EOF.
        private List<Record> Records(int from)
        {
            var list = new List<Record>();
            int depth = 0;
            for (int at = from; at + 4 <= data.Length;)
            {
                int type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
                if (at + 4 + length > data.Length) throw Damaged();
                list.Add(new Record(type, at + 4, length));
                at += 4 + length;
                if (type == 0x0809) depth++;
                else if (type == 0x000A && --depth <= 0) break;   // EOF of this substream
            }
            return list;
        }

        // The index of the EOF that ends the substream whose BOF is at `start`.
        private static int SubstreamEnd(List<Record> records, int start)
        {
            int depth = 0;
            for (int i = start; i < records.Count; i++)
            {
                if (records[i].Type == 0x0809) depth++;
                else if (records[i].Type == 0x000A && --depth == 0) return i;
            }
            return records.Count - 1;
        }

        public DocumentView Read()
        {
            data = file.Read(file.Find("Workbook")!, Label);
            var globals = Records(0);
            if (globals.Count == 0 || globals[0].Type != 0x0809) throw Damaged();
            // Password protected: decrypted in memory with the password the user typed (LegacyEncryption).
            if (globals.FindIndex(r => r.Type == 0x002F) is int filepass and >= 0)
            {
                data = LegacyEncryption.DecryptWorkbook(data, globals[filepass].Offset, globals[filepass].Length);
                globals = Records(0);
            }
            var sheets = new List<(string Name, int State, int Type, int Offset)>();
            for (int i = 0; i < globals.Count; i++)
            {
                var (type, at, length) = globals[i];
                switch (type)
                {
                    case 0x0022: date1904 = length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) == 1; break;
                    case 0x041E when length >= 5:
                        custom[BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at))] = XlString(at + 2, at + length, twoByteCount: true, out _);
                        break;
                    case 0x0031 when length >= 16:
                        {
                            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
                            fonts.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6)) >= 600, (flags & 0x0002) != 0, data[at + 10] != 0, (flags & 0x0008) != 0,
                                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)), XlString(at + 14, at + length, twoByteCount: false, out _)));
                        }
                        break;
                    case 0x0092 when length >= 2:
                        for (int k = 0, count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); k < count && 8 + k < 64 && 2 + k * 4 + 4 <= length; k++)
                            palette[8 + k] = $"#{data[at + 2 + k * 4]:x2}{data[at + 3 + k * 4]:x2}{data[at + 4 + k * 4]:x2}";
                        break;
                    case 0x00E0 when length >= 20:
                        formats.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)));
                        horizontal.Add((data[at + 6] & 0x07) switch { 1 => 'l', 2 => 'c', 3 => 'r', 5 => 'l', 6 => 'c', 7 => 'l', _ => '\0' });
                        xfRecords.Add((at, length));
                        break;
                    case 0x0085 when length >= 8:
                        sheets.Add((XlString(at + 6, at + length, twoByteCount: false, out _), data[at + 4] & 0x03, data[at + 5], BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at))));
                        break;
                    case 0x00FC: ReadStrings(globals, i); break;
                    case 0x00EB: drawingGroup = Joined(globals, i); break;
                    case 0x01AE when length >= 4: supBooks.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)) == 0x0401); break;
                    case 0x0017 when length >= 2:
                        for (int k = 0, count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); k < count && 2 + k * 6 + 6 <= length; k++)
                            externSheets.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2 + k * 6)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at + 4 + k * 6))));
                        break;
                }
            }
            foreach (var (at, _) in xfRecords) xfStyles.Add(styles.Add(Style(at)));   // after PALETTE and every FONT
            blips = BlipStore(drawingGroup);
            var view = new DocumentView { Kind = "sheet", Encoding = Label, CellStyles = styles.Table };
            int hidden = 0;
            var extra = new List<string>();
            // Charts first, so that reading the sheets can keep the cell values their series refer to.
            for (int tab = 0; tab < sheets.Count; tab++)
                if (sheets[tab].State == 0 && sheets[tab].Type is 0 or 2 && sheets[tab].Offset >= 0 && sheets[tab].Offset < data.Length) FindCharts(sheets[tab].Offset);
            for (int tab = 0; tab < sheets.Count; tab++)
            {
                var (name, state, type, offset) = sheets[tab];
                if (state != 0) { hidden++; continue; }
                if (type is not (0 or 2)) { extra.Add($"The macro sheet \"{name}\" is not shown in this version."); continue; }
                if (offset < 0 || offset >= data.Length) throw Damaged();
                view.Sheets.Add(type == 2 ? ReadChartSheet(name, offset) : ReadSheet(name, offset, view.Sheets.Count, tab));
            }
            if (view.Sheets.Count == 0) throw new DocumentException("This workbook has no visible worksheets to show.");
            ResolveCharts();
            if (file.Has("_VBA_PROJECT_CUR")) extra.Insert(0, "This workbook contains macros. They were ignored and never ran.");
            extra.AddRange(SheetDrawings.Notes(pictureBudget));
            if (ConditionalFormats.Note(conditionalNotShown) is { } conditionalNote) extra.Add(conditionalNote);
            if (conditionalOnLargeSheet) extra.Add(ConditionalFormats.LargeSheetNote);
            view.Notice = Notes(hidden, truncated, stored, extra);
            return view;
        }

        // A record's data joined with the CONTINUE records that follow it.
        private byte[] Joined(List<Record> records, int index)
        {
            var joined = new MemoryStream();
            joined.Write(data, records[index].Offset, records[index].Length);
            for (int i = index + 1; i < records.Count && records[i].Type == 0x003C && joined.Length < 64L * 1024 * 1024; i++) joined.Write(data, records[i].Offset, records[i].Length);
            return joined.ToArray();
        }

        // XF fill patterns 2 to 18 by their .xlsx names.
        private static readonly string[] XlsPatterns = ["mediumGray", "darkGray", "lightGray", "darkHorizontal", "darkVertical", "darkDown", "darkUp", "darkGrid", "darkTrellis",
            "lightHorizontal", "lightVertical", "lightDown", "lightUp", "lightGrid", "lightTrellis", "gray125", "gray0625"];

        private string? Colour(int icv) => icv is >= 0 and < 64 ? palette[icv] : null;   // 64 and above: system (automatic) colours

        private static string? Border(int kind, string? colour) => kind switch
        {
            0 => null,
            1 or 7 => $"1 solid {colour ?? "#000000"}", 2 => $"2 solid {colour ?? "#000000"}", 3 or 9 or 11 => $"1 dashed {colour ?? "#000000"}",
            4 => $"1 dotted {colour ?? "#000000"}", 5 => $"3 solid {colour ?? "#000000"}", 6 => $"3 double {colour ?? "#000000"}", _ => $"2 dashed {colour ?? "#000000"}"
        };

        // A cell format (XF record) as a CellStyle: its font, fill, borders, wrapping, vertical alignment and indent.
        private CellStyle Style(int at)
        {
            int fontIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            if (fontIndex >= 4) fontIndex--;                        // BIFF has no font 4
            var font = fontIndex >= 0 && fontIndex < fonts.Count ? fonts[fontIndex] : default;
            var baseFont = fonts.Count > 0 ? fonts[0] : default;
            uint lines = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 10)), more = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 14));
            int colours = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 18));
            // Fill pattern: 1 solid (the pattern colour fills the cell), 2-18 Excel's other patterns over the background colour.
            var (fill, pattern) = (more >> 26) switch
            {
                0 => ((string?)null, (string?)null),
                1 => (Colour(colours & 0x7F), null),
                var kind and <= 18 => WorkbookStyles.PatternFill(XlsPatterns[kind - 2], Colour(colours & 0x7F), Colour((colours >> 7) & 0x7F)),
                _ => (null, null)
            };
            string? colour = fonts.Count > 0 && font.Colour != baseFont.Colour ? Colour(font.Colour) : null;
            return new CellStyle
            {
                Bold = font.Bold, Italic = font.Italic, Underline = font.Underline, Strike = font.Strike, Color = colour,
                Size = font.Height > 0 && baseFont.Height > 0 && font.Height != baseFont.Height ? Math.Round((double)font.Height / baseFont.Height, 3) : 0,
                Font = font.Name is { } n && n != baseFont.Name ? WorkbookStyles.SafeName(n) : null,
                Fill = fill, Pattern = pattern,
                Wrap = (data[at + 6] & 0x08) != 0,
                VAlign = ((data[at + 6] >> 4) & 0x07) switch { 0 => "top", 1 => "middle", _ => null },
                Indent = data[at + 8] & 0x0F,
                Left = Border((int)(lines & 0xF), Colour((int)((lines >> 16) & 0x7F))), Right = Border((int)((lines >> 4) & 0xF), Colour((int)((lines >> 23) & 0x7F))),
                Top = Border((int)((lines >> 8) & 0xF), Colour((int)(more & 0x7F))), Bottom = Border((int)((lines >> 12) & 0xF), Colour((int)((more >> 7) & 0x7F)))
            };
        }

        // The shared string table, which continues across CONTINUE records; each continuation of a string's
        // characters starts with its own flags byte.
        private void ReadStrings(List<Record> records, int index)
        {
            var chunks = new List<(int Start, int End)> { (records[index].Offset, records[index].Offset + records[index].Length) };
            for (int i = index + 1; i < records.Count && records[i].Type == 0x003C; i++) chunks.Add((records[i].Offset, records[i].Offset + records[i].Length));
            int chunk = 0, at = chunks[0].Start + 8;
            int unique = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(chunks[0].Start + 4));
            long characters = 0;
            void Need(int bytes) { if (at + bytes > chunks[chunk].End) { if (at != chunks[chunk].End || ++chunk >= chunks.Count) throw Damaged(); at = chunks[chunk].Start; } }
            byte Byte() { Need(1); return data[at++]; }
            int Int16() { Need(2); int v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); at += 2; return v; }
            int Int32() { Need(4); int v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)); at += 4; return v; }
            void Skip(long bytes)
            {
                while (bytes > 0)
                {
                    if (at == chunks[chunk].End) { if (++chunk >= chunks.Count) throw Damaged(); at = chunks[chunk].Start; }
                    int take = (int)Math.Min(bytes, chunks[chunk].End - at); at += take; bytes -= take;
                }
            }
            for (int s = 0; s < unique && s < 4_000_000; s++)
            {
                int count = Int16(); byte flags = Byte();
                int runs = (flags & 0x08) != 0 ? Int16() : 0;
                int extended = (flags & 0x04) != 0 ? Int32() : 0;
                bool wide = (flags & 0x01) != 0;
                var text = new StringBuilder(count);
                while (text.Length < count)
                {
                    if (at == chunks[chunk].End) { if (++chunk >= chunks.Count) throw Damaged(); at = chunks[chunk].Start; wide = (data[at++] & 0x01) != 0; }
                    int available = (chunks[chunk].End - at) / (wide ? 2 : 1), take = Math.Min(count - text.Length, available);
                    if (take <= 0) throw Damaged();
                    text.Append(wide ? Encoding.Unicode.GetString(data, at, take * 2) : Encoding.Latin1.GetString(data, at, take));
                    at += take * (wide ? 2 : 1);
                }
                Skip(runs * 4L); Skip(extended);
                characters += count;
                if (characters > 32L * 1024 * 1024) throw new DocumentException("This workbook contains more text than this viewer can show safely.");
                strings.Add(text.ToString());
            }
        }

        // XLUnicodeString (two-byte count) or ShortXLUnicodeString (one-byte count) within one record.
        private string XlString(int at, int end, bool twoByteCount, out int next)
        {
            int count = twoByteCount ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : data[at];
            at += twoByteCount ? 2 : 1;
            bool wide = (data[at++] & 0x01) != 0;
            int bytes = count * (wide ? 2 : 1);
            if (at + bytes > end) throw Damaged();
            next = at + bytes;
            return wide ? Encoding.Unicode.GetString(data, at, bytes) : Encoding.Latin1.GetString(data, at, bytes);
        }

        private string Number(double value, int xf) =>
            Spreadsheets.FormatValue(value, xf >= 0 && xf < formats.Count ? formats[xf] : 0, custom, culture, date1904, cache);

        private char Align(int xf, char natural) => xf >= 0 && xf < horizontal.Count && horizontal[xf] != '\0' ? horizontal[xf] : natural;
        private int StyleOf(int xf) => xf >= 0 && xf < xfStyles.Count ? xfStyles[xf] : 0;

        private static double Rk(uint rk)
        {
            double value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
            return (rk & 0x01) != 0 ? value / 100 : value;
        }

        private static string Error(byte code) => code switch
        {
            0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", _ => "#N/A"
        };

        private SheetData ReadSheet(string name, int offset, int index, int tab)
        {
            var records = Records(offset);
            if (records.Count == 0 || records[0].Type != 0x0809) throw Damaged();
            using var sheet = new SheetBuilder(name, index, storeFolder, styles);
            var wanted = chartRanges.GetValueOrDefault(tab);
            var conditional = new ConditionalFormats();
            var conditionRanges = new List<int[]>();
            int conditionPriority = 0;
            void Cell(int row, int column, string text, int xf, char natural, double? value = null, bool error = false)
            {
                sheet.Add(row, column, text, Align(xf, natural), StyleOf(xf), ref budget);
                sheet.Value(row, column, value, error);
                // Cells a chart refers to keep their saved value for the chart.
                if (wanted is not null && chartCells.Count < 200_000 && wanted.Any(r => row >= r.Row1 && row <= r.Row2 && column >= r.Column1 && column <= r.Column2))
                    chartCells[(tab, row, column)] = (value, text);
            }
            var drawing = new MemoryStream();
            var objectTexts = new Dictionary<int, TextObject>();                  // by object index
            var objects = new List<(int Kind, int Chart)>();                    // OBJ records in order: kind, and the chart substream's BOF offset
            for (int i = 1; i < records.Count; i++)
            {
                var (type, at, length) = records[i];
                if (type == 0x0809)
                {
                    // A nested substream (an embedded chart) belongs to the object before it; its records are not cells.
                    if (objects.Count > 0 && objects[^1].Kind == 5) objects[^1] = (5, at);
                    i = SubstreamEnd(records, i);
                    continue;
                }
                if (type == 0x00EC && drawing.Length < 64L * 1024 * 1024)
                {
                    drawing.Write(data, at, length);
                    for (; i + 1 < records.Count && records[i + 1].Type == 0x003C; i++) drawing.Write(data, records[i + 1].Offset, records[i + 1].Length);
                    continue;
                }
                if (type == 0x005D && length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) == 0x0015)
                { objects.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)), -1)); continue; }
                if (type == 0x01B6 && length >= 14 && objects.Count > 0)
                {
                    // TXO: the text of the object before it, in the CONTINUE records that follow (the characters, each
                    // record starting with its own flags byte; then 8-byte formatting runs: first character and font).
                    int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10)), runBytes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12));
                    var text = new StringBuilder();
                    var runs = new List<(int Char, int Font)>();
                    int k = i + 1;
                    while (text.Length < count && k < records.Count && records[k].Type == 0x003C && records[k].Length >= 1)
                    {
                        var (_, part, partLength) = records[k++];
                        bool wide = (data[part] & 1) != 0;
                        int chars = Math.Min(count - text.Length, (partLength - 1) / (wide ? 2 : 1));
                        if (chars <= 0) break;
                        text.Append(wide ? Encoding.Unicode.GetString(data, part + 1, chars * 2) : Encoding.Latin1.GetString(data, part + 1, chars));
                    }
                    for (int read = 0; read < runBytes && k < records.Count && records[k].Type == 0x003C; k++)
                    {
                        var (_, part, partLength) = records[k];
                        for (int p = 0; p + 8 <= partLength && runs.Count < 1000; p += 8)
                            runs.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + p)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + p + 2))));
                        read += partLength;
                    }
                    objectTexts[objects.Count - 1] = new TextObject(text.ToString(), runs, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)));
                    i = k - 1;
                    continue;
                }
                int Row() => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                int Column() => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
                int Xf() => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
                switch (type)
                {
                    case 0x0203 when length >= 14:
                        { double v = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at + 6)); Cell(Row(), Column(), Number(v, Xf()), Xf(), 'r', v); }
                        break;
                    case 0x027E when length >= 10:
                        { double v = Rk(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 6))); Cell(Row(), Column(), Number(v, Xf()), Xf(), 'r', v); }
                        break;
                    case 0x00BD when length >= 6:
                        for (int k = 0, first = Column(); 4 + k * 6 + 6 <= length - 2; k++)
                        {
                            int xf = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4 + k * 6));
                            double v = Rk(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 6 + k * 6)));
                            Cell(Row(), first + k, Number(v, xf), xf, 'r', v);
                        }
                        break;
                    case 0x0201 when length >= 6: Cell(Row(), Column(), "", Xf(), 'l'); break;                    // empty, formatted
                    case 0x00BE when length >= 6:
                        for (int k = 0, first = Column(); 4 + k * 2 + 2 <= length - 2; k++) Cell(Row(), first + k, "", BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4 + k * 2)), 'l');
                        break;
                    case 0x00FD when length >= 10:
                        int string_ = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 6));
                        Cell(Row(), Column(), string_ >= 0 && string_ < strings.Count ? strings[string_] : "", Xf(), 'l');
                        break;
                    case 0x0204 when length >= 9: Cell(Row(), Column(), XlString(at + 6, at + length, twoByteCount: true, out _), Xf(), 'l'); break;
                    case 0x0205 when length >= 8: Cell(Row(), Column(), data[at + 7] == 0 ? (data[at + 6] != 0 ? "TRUE" : "FALSE") : Error(data[at + 6]), Xf(), 'c', error: data[at + 7] != 0); break;
                    case 0x0006 when length >= 20:
                        // The saved result: a number, or (when the last two bytes are 0xFFFF) a string in the next STRING
                        // record, a boolean, an error or an empty string.
                        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12)) != 0xFFFF)
                        { double v = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at + 6)); Cell(Row(), Column(), Number(v, Xf()), Xf(), 'r', v); }
                        else
                            switch (data[at + 6])
                            {
                                case 0:
                                    if (i + 1 < records.Count && records[i + 1].Type == 0x0207 && records[i + 1].Length >= 3)
                                        Cell(Row(), Column(), XlString(records[i + 1].Offset, records[i + 1].Offset + records[i + 1].Length, twoByteCount: true, out _), Xf(), 'l');
                                    break;
                                case 1: Cell(Row(), Column(), data[at + 8] != 0 ? "TRUE" : "FALSE", Xf(), 'c'); break;
                                case 2: Cell(Row(), Column(), Error(data[at + 8]), Xf(), 'c', error: true); break;
                            }
                        break;
                    case 0x0208 when length >= 16:
                        if ((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12)) & 0x0020) != 0) sheet.Sheet.HiddenRows.Add(Row() + 1);
                        // fGhostDirty: the row has its own format (XF in the low 12 bits after the flags).
                        if ((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12)) & 0x0080) != 0)
                            sheet.RowStyle(Row(), StyleOf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 14)) & 0x0FFF));
                        // ROW: the height in twips (1/20 point) in the low 15 bits.
                        Spreadsheets.RowHeight(sheet.Sheet, Row() + 1, (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6)) & 0x7FFF) / 20.0);
                        break;
                    case 0x0225 when length >= 4:                                               // DEFAULTROWHEIGHT, in twips
                        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)) is > 0 and < 8200 and var twips) sheet.Sheet.DefaultRowHeight = twips / 20.0;
                        break;
                    case 0x007D when length >= 10:
                        {
                            int first = Row(), last = Math.Min(Column(), Spreadsheets.MaxColumns - 1);
                            double width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)) / 256.0;
                            bool isHidden = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 8)) & 0x0001) != 0;
                            for (int c = first; c <= last; c++) sheet.Widths[c] = isHidden ? 0 : width;
                            sheet.ColumnStyle(first, Column(), StyleOf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 6))));
                        }
                        break;
                    case 0x0055 when length >= 2: sheet.DefaultWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) + 0.71; break;
                    case 0x00E5 when length >= 2:
                        for (int k = 0, count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); k < count && 2 + k * 8 + 8 <= length; k++)
                        {
                            var span = data.AsSpan(at + 2 + k * 8);
                            sheet.Sheet.Merges.Add([BinaryPrimitives.ReadUInt16LittleEndian(span), BinaryPrimitives.ReadUInt16LittleEndian(span[4..]),
                                BinaryPrimitives.ReadUInt16LittleEndian(span[2..]), BinaryPrimitives.ReadUInt16LittleEndian(span[6..])]);
                        }
                        break;
                    case 0x01B0: conditionRanges = ConditionRanges(at, length); break;                // CONDFMT
                    case 0x01B1: ReadCondition(at, length, conditionRanges, ++conditionPriority, conditional); break;   // CF
                    case 0x087A: conditional.NotShown++; break;                                  // CF12 (Excel 2007 rules): not read
                    case 0x023E when length >= 2: sheet.Sheet.RightToLeft = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) & 0x0040) != 0; break;
                    case 0x0041 when length >= 4:
                        sheet.Sheet.FrozenColumns = Math.Min(Spreadsheets.MaxColumns, (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)));
                        sheet.Sheet.FrozenRows = Math.Min(Spreadsheets.MaxRowsPerSheet, (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)));
                        break;
                }
            }
            sheet.ApplyDefaultStyles(255, ref budget);                              // Excel 97-2003's last column is IV (255)
            if (!sheet.ApplyConditional(conditional)) conditionalOnLargeSheet = true;
            conditionalNotShown += conditional.NotShown;
            var built = sheet.Build();
            truncated |= sheet.Truncated; stored |= built.Store.Length > 0;
            built.Pictures = Drawings(built, index, drawing.ToArray(), objects, objectTexts, storeFolder);
            if (built.Pictures.Count > 0 && built.RowCount == 0) built.Notice = "";
            return built;
        }
    }

    // ---- OpenDocument spreadsheet ----

    // Each cell's paragraphs are the text the producing application displayed when it saved the file, so they are
    // shown as they are (no number formatting is re-applied and no formula is evaluated). Cell styles come from the
    // named styles (styles.xml) and the automatic styles (content.xml), following their parents.
    private sealed class OpenDocumentSheets(ZipArchive zip, string? storeFolder)
    {
        private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        private const string StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        private const string FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
        private const string DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        private readonly Dictionary<string, double> columnWidths = [];   // column style -> Excel character width
        private readonly HashSet<string> hiddenTables = [];              // table styles with display="false"
        private readonly Dictionary<string, Props> cellStyles = [];      // cell style name -> its own properties
        private readonly Dictionary<string, (int Style, char Align)> resolved = [];
        private readonly StyleTable styles = new();
        private int budget = Spreadsheets.MaxCellsPerWorkbook;
        private bool truncated, stored;
        private readonly SheetDrawings.Budget pictureBudget = new();
        private List<SheetPicture> pictures = [], pageAnchored = [];
        private OpenDocumentDrawings.ShapeStyles shapeStyles = new();
        private int sheetIndex;
        private readonly Dictionary<string, double> rowStyleHeights = [];            // row style -> height in points
        private readonly List<(int Row, int Count, double Points)> rowHeights = [];  // the current sheet's styled rows
        private readonly Dictionary<double, int> heightCounts = [];                   // height -> number of rows
        private readonly Dictionary<string, string> displayNames = [];                // cell style display name -> name
        private ConditionalFormats conditional = new();                               // the current sheet's
        private int conditionalNotShown;
        private bool conditionalOnLargeSheet;

        // Properties a cell style sets itself (null: inherited from its parent).
        private sealed class Props
        {
            public string? Parent;
            public bool? Bold, Italic, Underline, Strike, Wrap;
            public string? Color, Fill, Font, VAlign, Top, Right, Bottom, Left;
            public double? Size;
            public char? Align;
        }

        public DocumentView Read()
        {
            ArchiveSafety.Validate(zip, maximumBytes: 2L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
            if (zip.GetEntry("META-INF/manifest.xml") is { } manifest)
                using (var reader = new StreamReader(manifest.Open()))
                    if (reader.ReadToEnd().Contains("encryption-data", StringComparison.Ordinal))
                        throw new DocumentException("This spreadsheet is protected with a password. Password-protected files cannot be opened in this version. Remove the password in the application that made it, or ask the sender for an unprotected copy.");
            var content = zip.GetEntry("content.xml") ?? throw Damaged();
            shapeStyles = OpenDocumentDrawings.ReadShapeStyles(zip);
            if (zip.GetEntry("styles.xml") is { } common) using (var r = Open(common)) while (r.Read()) if (r.NodeType == XmlNodeType.Element && r.LocalName == "style") ReadStyle(r);
            var frozen = ReadFrozen();
            var view = new DocumentView { Kind = "sheet", Encoding = "OpenDocument spreadsheet", CellStyles = styles.Table };
            int hidden = 0;
            var extra = new List<string>();
            if (zip.Entries.Any(e => e.FullName.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) || e.FullName.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase)))
                extra.Add("This spreadsheet contains macros. They were ignored and never ran.");
            using (var r = Open(content))
            {
                SheetBuilder? sheet = null;
                var columnDefaults = new List<string?>();
                int row = -1;
                try
                {
                    while (r.Read())
                    {
                        if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "table" && sheet is not null)
                        { view.Sheets.Add(Finish(sheet)); sheet.Dispose(); sheet = null; continue; }
                        if (r.NodeType != XmlNodeType.Element) continue;
                        switch (r.LocalName)
                        {
                            case "style" when sheet is null: ReadStyle(r); break;
                            case "table" when r.NamespaceURI == TableNs:
                                string name = r.GetAttribute("name", TableNs) ?? $"Sheet{view.Sheets.Count + hidden + 1}";
                                if (hiddenTables.Contains(r.GetAttribute("style-name", TableNs) ?? "")) { hidden++; r.Skip(); continue; }
                                sheet = new SheetBuilder(name, view.Sheets.Count, storeFolder, styles); row = -1; columnDefaults.Clear();
                                sheetIndex = view.Sheets.Count; pictures = []; pageAnchored = []; rowHeights.Clear(); heightCounts.Clear(); conditional = new();
                                if (frozen.TryGetValue(name, out var split)) { sheet.Sheet.FrozenColumns = split.Columns; sheet.Sheet.FrozenRows = split.Rows; }
                                if (r.IsEmptyElement) { view.Sheets.Add(sheet.Build()); sheet.Dispose(); sheet = null; }
                                break;
                            case "shapes" when sheet is not null && r.NamespaceURI == TableNs:
                                // Frames anchored to the sheet rather than to a cell.
                                using (var subtree = r.ReadSubtree())
                                    foreach (var drawn in XElement.Load(subtree).Elements())
                                        if (drawn.Name == XName.Get("frame", DrawNs))
                                        { if (OpenDocumentDrawings.Frame(drawn, zip, storeFolder, sheetIndex, pictureBudget, pictures, 0, 0) is { } placed) pageAnchored.Add(placed); }
                                        else pageAnchored.AddRange(OpenDocumentDrawings.Shapes(drawn, shapeStyles, pictures, 0, 0));
                                break;
                            case "conditional-formats" when sheet is not null && r.NamespaceURI == CalcExtNs:
                                using (var subtree = r.ReadSubtree()) ReadConditions(XElement.Load(subtree), conditional, ConditionStyle);
                                break;
                            case "table-column" when sheet is not null:
                                {
                                    int repeat = Repeat(r, "number-columns-repeated");
                                    double width = columnWidths.GetValueOrDefault(r.GetAttribute("style-name", TableNs) ?? "", sheet.DefaultWidth);
                                    if (r.GetAttribute("visibility", TableNs) is "collapse" or "filter") width = 0;
                                    string? defaultStyle = r.GetAttribute("default-cell-style-name", TableNs);
                                    // A formatted column (not a run reaching the sheet's last column): its empty cells are filled
                                    // within the data after the sheet is read (SheetBuilder.ApplyDefaultStyles).
                                    if (defaultStyle is not null && columnDefaults.Count < Spreadsheets.MaxColumns)
                                        sheet.ColumnStyle(columnDefaults.Count, columnDefaults.Count + repeat - 1, Resolve(defaultStyle).Style);
                                    for (int k = 0; k < repeat && columnDefaults.Count < Spreadsheets.MaxColumns; k++)
                                    { sheet.Widths[columnDefaults.Count] = width; columnDefaults.Add(defaultStyle); }
                                }
                                break;
                            case "table-row" when sheet is not null:
                                {
                                    int repeat = Repeat(r, "number-rows-repeated");
                                    bool hiddenRow = r.GetAttribute("visibility", TableNs) is "collapse" or "filter";
                                    if (r.IsEmptyElement) { row += repeat; break; }
                                    row++;
                                    if (hiddenRow) sheet.Sheet.HiddenRows.Add(row + 1);
                                    if (rowStyleHeights.TryGetValue(r.GetAttribute("style-name", TableNs) ?? "", out double points))
                                    {
                                        heightCounts[points] = heightCounts.GetValueOrDefault(points) + repeat;
                                        if (repeat <= 1000) rowHeights.Add((row + 1, repeat, points));
                                    }
                                    ReadRow(r, sheet, row, repeat, columnDefaults);
                                    if (repeat > 1) row += repeat - 1;
                                }
                                break;
                        }
                    }
                }
                finally { sheet?.Dispose(); }
            }
            if (view.Sheets.Count == 0) throw new DocumentException("This spreadsheet has no visible sheets to show.");
            extra.AddRange(SheetDrawings.Notes(pictureBudget));
            if (ConditionalFormats.Note(conditionalNotShown) is { } conditionalNote) extra.Add(conditionalNote);
            if (conditionalOnLargeSheet) extra.Add(ConditionalFormats.LargeSheetNote);
            view.Notice = Notes(hidden, truncated, stored, extra);
            return view;
        }

        // The built sheet with its pictures and charts; those anchored to the sheet get their cell from the column widths.
        private SheetData Finish(SheetBuilder sheet)
        {
            sheet.ApplyDefaultStyles(WholeSheetColumns - 1, ref budget);
            if (!sheet.ApplyConditional(conditional)) conditionalOnLargeSheet = true;
            conditionalNotShown += conditional.NotShown;
            var built = sheet.Build();
            truncated |= sheet.Truncated; stored |= built.Store.Length > 0;
            // Every row has a row style: the most common height is the sheet's default, the others are recorded.
            if (heightCounts.Count > 0)
            {
                built.DefaultRowHeight = heightCounts.MaxBy(pair => pair.Value).Key;
                foreach (var (first, count, points) in rowHeights)
                    for (int k = 0; k < count; k++) Spreadsheets.RowHeight(built, first + k, points);
            }
            foreach (var picture in pageAnchored)
                (picture.Column, picture.ColumnOffset, picture.Row, picture.RowOffset) = SheetDrawings.CellAt(built, picture.ColumnOffset, picture.RowOffset);
            // Shapes carry offsets that may run past their cell and a size: their start and end become cells.
            foreach (var picture in pictures) if (picture.Shape is not null) SheetDrawings.Normalize(built, picture);
            built.Pictures = pictures;
            if (pictures.Count > 0 && built.RowCount == 0) built.Notice = "";
            return built;
        }

        private static XmlReader Open(ZipArchiveEntry entry) => XmlReader.Create(entry.Open(), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true });

        // OpenDocument sheets have 1,024 columns (older files) or 16,384; a run reaching column 1,024 counts as the whole row.
        private const int WholeSheetColumns = 1024;

        private static int Repeat(XmlReader r, string attribute) =>
            int.TryParse(r.GetAttribute(attribute, TableNs), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;

        // A style: column widths (in Excel character units, as the grid expects), hidden tables, and cell properties.
        private void ReadStyle(XmlReader r)
        {
            string name = r.GetAttribute("name", StyleNs) ?? "";
            bool cell = r.GetAttribute("family", StyleNs) == "table-cell";
            var props = new Props { Parent = r.GetAttribute("parent-style-name", StyleNs) };
            if (cell && name.Length > 0 && r.GetAttribute("display-name", StyleNs) is { Length: > 0 } display && displayNames.Count < 10_000) displayNames[display] = name;
            if (!r.IsEmptyElement)
            {
                int depth = r.Depth;
                while (r.Read() && r.Depth > depth)
                {
                    if (r.NodeType != XmlNodeType.Element) continue;
                    switch (r.LocalName)
                    {
                        case "table-column-properties" when Length(r.GetAttribute("column-width", StyleNs)) is double px:
                            columnWidths[name] = Math.Max(0, Math.Round((px - 5) / 7, 2)); break;
                        case "table-row-properties" when Length(r.GetAttribute("row-height", StyleNs)) is double rowPx:
                            rowStyleHeights[name] = Math.Round(rowPx * 72 / 96, 2); break;
                        case "table-properties" when r.GetAttribute("display", TableNs) == "false": hiddenTables.Add(name); break;
                        case "text-properties" when cell:
                            if (r.GetAttribute("font-weight", FoNs) is { } weight) props.Bold = weight == "bold" || (int.TryParse(weight, out int w) && w >= 600);
                            if (r.GetAttribute("font-style", FoNs) is { } italic) props.Italic = italic == "italic";
                            if (r.GetAttribute("text-underline-style", StyleNs) is { } underline) props.Underline = underline != "none";
                            if (r.GetAttribute("text-line-through-style", StyleNs) is { } strike) props.Strike = strike != "none";
                            if (WorkbookStyles.Hex(r.GetAttribute("color", FoNs)?.TrimStart('#')) is { } colour) props.Color = colour;
                            if (Length(r.GetAttribute("font-size", FoNs)) is double size) props.Size = size;
                            if (r.GetAttribute("font-name", StyleNs) is { } font) props.Font = WorkbookStyles.SafeName(font);
                            break;
                        case "table-cell-properties" when cell:
                            if (r.GetAttribute("background-color", FoNs) is { } fill) props.Fill = fill == "transparent" ? "" : WorkbookStyles.Hex(fill.TrimStart('#'));
                            if (r.GetAttribute("wrap-option", FoNs) is { } wrap) props.Wrap = wrap == "wrap";
                            if (r.GetAttribute("vertical-align", StyleNs) is { } vertical) props.VAlign = vertical is "top" or "middle" ? vertical : "";
                            if (r.GetAttribute("border", FoNs) is { } all) props.Top = props.Right = props.Bottom = props.Left = Border(all);
                            if (r.GetAttribute("border-top", FoNs) is { } top) props.Top = Border(top);
                            if (r.GetAttribute("border-right", FoNs) is { } right) props.Right = Border(right);
                            if (r.GetAttribute("border-bottom", FoNs) is { } bottom) props.Bottom = Border(bottom);
                            if (r.GetAttribute("border-left", FoNs) is { } left) props.Left = Border(left);
                            break;
                        case "paragraph-properties" when cell && r.GetAttribute("text-align", FoNs) is { } align:
                            props.Align = align switch { "center" => 'c', "end" or "right" => 'r', _ => 'l' }; break;
                    }
                }
            }
            if (cell && name.Length > 0) cellStyles[name] = props;
        }

        // "0.74pt solid #000000" as the grid's border form ("" for none).
        private static string Border(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Contains("none")) return "";
            double px = parts.Select(Length).FirstOrDefault(v => v is not null) ?? 1;
            string kind = parts.FirstOrDefault(p => p is "solid" or "dashed" or "dotted" or "double") ?? "solid";
            string colour = parts.Select(p => WorkbookStyles.Hex(p.TrimStart('#'))).FirstOrDefault(c => c is not null) ?? "#000000";
            return $"{(kind == "double" ? 3 : px <= 1.4 ? 1 : px <= 2.7 ? 2 : 3)} {kind} {colour}";
        }

        // The effective style of a named cell style: its own properties over its parents', compared with "Default".
        private (int Style, char Align) Resolve(string? name)
        {
            name ??= "Default";
            if (resolved.TryGetValue(name, out var known)) return known;
            var chain = new List<Props>();
            for (string? n = name; n is not null && chain.Count < 16 && cellStyles.TryGetValue(n, out var p); n = p.Parent) chain.Add(p);
            T? Pick<T>(Func<Props, T?> get) where T : class => chain.Select(get).FirstOrDefault(v => v is not null);
            T? PickValue<T>(Func<Props, T?> get) where T : struct => chain.Select(get).FirstOrDefault(v => v.HasValue);
            var baseProps = cellStyles.GetValueOrDefault("Default");
            string? colour = Pick(p => p.Color), fill = Pick(p => p.Fill), font = Pick(p => p.Font);
            double? size = PickValue(p => p.Size), baseSize = baseProps?.Size;
            var style = new CellStyle
            {
                Bold = PickValue(p => p.Bold) ?? false, Italic = PickValue(p => p.Italic) ?? false,
                Underline = PickValue(p => p.Underline) ?? false, Strike = PickValue(p => p.Strike) ?? false,
                Color = colour is not null && colour != baseProps?.Color ? colour : null,     // the default text colour stays automatic
                Fill = fill is { Length: > 0 } ? fill : null,
                Font = font is not null && font != baseProps?.Font ? font : null,
                Size = size is > 0 && baseSize is > 0 && Math.Abs(size.Value - baseSize.Value) > 0.01 ? Math.Round(size.Value / baseSize.Value, 3) : 0,
                Wrap = PickValue(p => p.Wrap) ?? false, VAlign = Pick(p => p.VAlign) is { Length: > 0 } v ? v : null,
                Top = Pick(p => p.Top) is { Length: > 0 } t ? t : null, Right = Pick(p => p.Right) is { Length: > 0 } rr ? rr : null,
                Bottom = Pick(p => p.Bottom) is { Length: > 0 } b ? b : null, Left = Pick(p => p.Left) is { Length: > 0 } l ? l : null
            };
            var result = (styles.Add(style), PickValue(p => p.Align) ?? '\0');
            resolved[name] = result;
            return result;
        }

        // A conditional format's cell style (named by its display name) as the properties it sets itself or inherits from
        // parents other than "Default"; null when there is no such style.
        private WorkbookStyles.Dxf? ConditionStyle(string displayName)
        {
            string name = displayNames.GetValueOrDefault(displayName) ?? displayName;
            var chain = new List<Props>();
            for (string? n = name; n is not null && n != "Default" && chain.Count < 16 && cellStyles.TryGetValue(n, out var p); n = p.Parent) chain.Add(p);
            if (chain.Count == 0) return null;
            string? Pick(Func<Props, string?> get) => chain.Select(get).FirstOrDefault(v => v is not null) is { Length: > 0 } v ? v : null;
            bool? PickValue(Func<Props, bool?> get) => chain.Select(get).FirstOrDefault(v => v.HasValue);
            return new WorkbookStyles.Dxf
            {
                Bold = PickValue(p => p.Bold), Italic = PickValue(p => p.Italic), Underline = PickValue(p => p.Underline), Strike = PickValue(p => p.Strike),
                Color = Pick(p => p.Color), Fill = Pick(p => p.Fill),
                Top = Pick(p => p.Top), Right = Pick(p => p.Right), Bottom = Pick(p => p.Bottom), Left = Pick(p => p.Left)
            };
        }

        // A length such as "2.258cm" in pixels at 96 dpi (font sizes in points use the same unit).
        private static double? Length(string? value)
        {
            if (value is null) return null;
            foreach (var (unit, factor) in new[] { ("cm", 96 / 2.54), ("mm", 96 / 25.4), ("in", 96.0), ("pt", 96 / 72.0), ("pc", 16.0), ("px", 1.0) })
                if (value.EndsWith(unit, StringComparison.Ordinal) && double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return n * factor;
            return null;
        }

        private void ReadRow(XmlReader r, SheetBuilder sheet, int row, int rowRepeat, List<string?> columnDefaults)
        {
            int depth = r.Depth, column = 0;
            var cells = new List<(int Column, string Text, char Align, int Style, double? Number, bool Error)>();
            while (r.Read() && r.Depth > depth)
            {
                if (r.NodeType != XmlNodeType.Element || r.Depth != depth + 1) continue;
                if (r.LocalName is not ("table-cell" or "covered-table-cell")) { r.Skip(); continue; }
                int repeat = Repeat(r, "number-columns-repeated");
                string type = r.GetAttribute("value-type", OfficeNs) ?? "";
                string? own = r.GetAttribute("style-name", TableNs);
                var (style, align) = Resolve(own ?? (column < columnDefaults.Count ? columnDefaults[column] : null));
                int spanColumns = Repeat(r, "number-columns-spanned"), spanRows = Repeat(r, "number-rows-spanned");
                if (r.LocalName == "table-cell" && (spanColumns > 1 || spanRows > 1) && column < Spreadsheets.MaxColumns && row < Spreadsheets.MaxStoredRows)
                    sheet.Sheet.Merges.Add([row, column, row + spanRows - 1, Math.Min(Spreadsheets.MaxColumns - 1, column + spanColumns - 1)]);
                double? number = SavedNumber(r, type);
                bool formula = r.GetAttribute("formula", TableNs) is not null;
                string text = CellText(r, row, column);
                bool error = formula && number is null && IsError(text);
                char natural = type switch { "float" or "percentage" or "currency" or "date" or "time" => 'r', "boolean" => 'c', _ => 'l' };
                // Empty cells: a column's own format is filled in later within the data (so repeated empty rows add nothing),
                // and a formatted run reaching the sheet's last column formats the whole row.
                if (text.Length == 0 && own is null) { column += repeat; continue; }
                if (text.Length == 0 && column + repeat >= WholeSheetColumns)
                {
                    if (styles.Table[style].Visible) for (int k = 0; k < rowRepeat && k < 1000; k++) sheet.RowStyle(row + k, style);
                    column += repeat; continue;
                }
                if (text.Length > 0 || styles.Table[style].Visible)
                    for (int k = 0; k < repeat && column + k < Spreadsheets.MaxColumns && k < 1024; k++) cells.Add((column + k, text, align == '\0' ? natural : align, style, number, error));
                column += repeat;
            }
            // Rows repeated with the same content (rare apart from empty rows) are laid out, within the row limit.
            for (int k = 0; k < rowRepeat && (k == 0 || cells.Count > 0); k++)
            {
                if (row + k >= Spreadsheets.MaxStoredRows) { if (cells.Count > 0) sheet.Truncated = true; break; }
                foreach (var (c, t, a, s, number, error) in cells) { sheet.Add(row + k, c, t, a, s, ref budget); sheet.Value(row + k, c, number, error); }
            }
        }

        // The saved number of a number, percentage, currency, date or time cell (dates as Excel serial numbers, times as
        // fractions of a day), for conditional formatting.
        private static double? SavedNumber(XmlReader r, string type)
        {
            try
            {
                return type switch
                {
                    "float" or "percentage" or "currency" when double.TryParse(r.GetAttribute("value", OfficeNs), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) => v,
                    "date" when DateTime.TryParse(r.GetAttribute("date-value", OfficeNs), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d.ToOADate(),
                    "time" when r.GetAttribute("time-value", OfficeNs) is { } time => XmlConvert.ToTimeSpan(time).TotalDays,
                    _ => null
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { return null; }
        }

        private static bool IsError(string text) =>
            text is "#NULL!" or "#DIV/0!" or "#VALUE!" or "#REF!" or "#NAME?" or "#NUM!" or "#N/A" || text.StartsWith("Err:", StringComparison.Ordinal);

        // The cell's paragraphs (text:p) joined by line breaks, with <text:s/>, <text:tab/> and <text:line-break/>. Pictures,
        // charts and shapes anchored to the cell are not cell text: frames are read as drawings, other shapes are skipped.
        private string CellText(XmlReader r, int row, int column)
        {
            if (r.IsEmptyElement) return "";
            var text = new StringBuilder();
            int depth = r.Depth, paragraphs = 0;
            while (r.Read() && r.Depth > depth)
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == DrawNs)
                {
                    // A subtree reader leaves the main reader on the drawing's end, so the loop continues after it.
                    using var subtree = r.ReadSubtree();
                    if (r.LocalName == "frame") OpenDocumentDrawings.Frame(XElement.Load(subtree), zip, storeFolder, sheetIndex, pictureBudget, pictures, row, column);
                    else OpenDocumentDrawings.Shapes(XElement.Load(subtree), shapeStyles, pictures, row, column);
                }
                else if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "p": if (paragraphs++ > 0) text.Append('\n'); break;
                        case "s": text.Append(' ', Math.Min(1000, Spaces(r))); break;
                        case "tab": text.Append('\t'); break;
                        case "line-break": text.Append('\n'); break;
                        case "annotation": using (r.ReadSubtree()) { } break;   // comments are not cell text
                    }
                }
                else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace && r.Depth > depth + 1) text.Append(r.Value);
                if (text.Length > 32767) break;
            }
            return text.ToString();
        }

        private static int Spaces(XmlReader r) =>
            int.TryParse(r.GetAttribute("c", "urn:oasis:names:tc:opendocument:xmlns:text:1.0"), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;

        // Frozen rows and columns per sheet, from settings.xml (split mode 2 = frozen).
        private Dictionary<string, (int Columns, int Rows)> ReadFrozen()
        {
            var result = new Dictionary<string, (int, int)>();
            if (zip.GetEntry("settings.xml") is not { } settings) return result;
            const string ConfigNs = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
            using var r = Open(settings);
            string? table = null; var values = new Dictionary<string, string>();
            int tablesDepth = -1;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "config-item-map-named" && r.GetAttribute("name", ConfigNs) == "Tables") tablesDepth = r.Depth;
                else if (tablesDepth >= 0 && r.NodeType == XmlNodeType.Element && r.LocalName == "config-item-map-entry" && r.Depth == tablesDepth + 1)
                { table = r.GetAttribute("name", ConfigNs); values.Clear(); }
                else if (table is not null && r.NodeType == XmlNodeType.Element && r.LocalName == "config-item")
                    values[r.GetAttribute("name", ConfigNs) ?? ""] = r.ReadElementContentAsString();
                else if (table is not null && r.NodeType == XmlNodeType.EndElement && r.LocalName == "config-item-map-entry" && r.Depth == tablesDepth + 1)
                {
                    int Value(string key) => values.TryGetValue(key, out var v) && int.TryParse(v, out int n) ? n : 0;
                    int columns = Value("HorizontalSplitMode") == 2 ? Value("HorizontalSplitPosition") : 0, rows = Value("VerticalSplitMode") == 2 ? Value("VerticalSplitPosition") : 0;
                    if (columns > 0 || rows > 0) result[table] = (Math.Min(columns, Spreadsheets.MaxColumns), Math.Min(rows, Spreadsheets.MaxRowsPerSheet));
                    table = null;
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "config-item-map-named" && r.Depth == tablesDepth) tablesDepth = -1;
            }
            return result;
        }
    }
}
