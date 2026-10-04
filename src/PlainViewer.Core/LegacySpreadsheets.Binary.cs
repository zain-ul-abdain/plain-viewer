using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelNumberFormat;
namespace PlainViewer.Core;

// Excel binary workbooks (.xlsb, after 0.9.0): the .xlsx package layout with binary records ([MS-XLSB]) in place of
// XML. Read like the other spreadsheet formats: saved values and saved formula results (never recalculated), number and
// date formats, horizontal alignment, column widths and hidden columns, merged cells, frozen panes, hidden sheets.
// Not read from .xlsb: fonts, fills and borders, pictures, charts, shapes and conditional formatting (the notice says so).
public static partial class LegacySpreadsheets
{
    internal static DocumentView ReadBinary(ZipArchive zip, CultureInfo culture, string? storeFolder) => new BinaryWorkbook(zip, culture, storeFolder).Read();

    private sealed class BinaryWorkbook(ZipArchive zip, CultureInfo culture, string? storeFolder)
    {
        private const string Label = "Excel binary workbook";
        private readonly List<string> strings = [];
        private readonly Dictionary<int, string> custom = [];
        private readonly List<(int Format, char Align)> xfs = [];
        private readonly Dictionary<string, NumberFormat> cache = [];
        private readonly StyleTable styles = new();
        private bool date1904, truncated, stored;
        private int budget = Spreadsheets.MaxCellsPerWorkbook;

        // Records: a type and a size, each a little-endian base-128 number (type at most 2 bytes, size at most 4).
        private static IEnumerable<(int Type, int Offset, int Length)> Records(byte[] data)
        {
            for (int at = 0; at < data.Length;)
            {
                int type = data[at++] & 0x7F;
                if (at <= data.Length && (data[at - 1] & 0x80) != 0) { if (at >= data.Length) yield break; type |= (data[at++] & 0x7F) << 7; }
                int size = 0;
                for (int shift = 0; shift < 28; shift += 7)
                {
                    if (at >= data.Length) yield break;
                    byte b = data[at++]; size |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                }
                if (size < 0 || at + size > data.Length) throw Damaged();
                yield return (type, at, size);
                at += size;
            }
        }

        private static string WideString(byte[] data, ref int at, int end)
        {
            if (at + 4 > end) throw Damaged();
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)); at += 4;
            if (count == 0xFFFFFFFF) return "";
            if (count > (uint)(end - at) / 2) throw Damaged();
            string text = Encoding.Unicode.GetString(data, at, (int)count * 2); at += (int)count * 2;
            return text;
        }

        private byte[] Part(string name)
        {
            var entry = zip.GetEntry(name) ?? throw Damaged();
            if (entry.Length > 1024L * 1024 * 1024) throw new DocumentException("This workbook is larger than this viewer can open safely.");
            using var stream = entry.Open();
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        // Relationship targets of a part, by id, as paths in the package.
        private Dictionary<string, string> Relationships(string part)
        {
            var result = new Dictionary<string, string>();
            string folder = part.Contains('/') ? part[..(part.LastIndexOf('/') + 1)] : "";
            if (zip.GetEntry(folder + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels") is not { } rels) return result;
            using var stream = rels.Open();
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
            foreach (var e in XDocument.Load(reader).Root?.Elements() ?? [])
                if (e.Attribute("Id")?.Value is { } id && e.Attribute("Target")?.Value is { } target && e.Attribute("TargetMode")?.Value != "External")
                    result[id] = WebDocuments.Resolve(target.StartsWith('/') ? "" : folder, target.TrimStart('/'));
            return result;
        }

        public DocumentView Read()
        {
            ArchiveSafety.Validate(zip, maximumBytes: 4L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
            const string workbookPart = "xl/workbook.bin";
            var rels = Relationships(workbookPart);
            var sheets = new List<(string Name, int State, string? Part)>();
            byte[] workbook = Part(workbookPart);
            foreach (var (type, at, length) in Records(workbook))
            {
                int end = at + length, p = at;
                if (type == 0x0099 && length >= 4) date1904 = (BinaryPrimitives.ReadUInt32LittleEndian(workbook.AsSpan(at)) & 1) != 0;   // BrtWbProp
                else if (type == 0x009C && length >= 8)                                                                               // BrtBundleSh
                {
                    int state = (int)(BinaryPrimitives.ReadUInt32LittleEndian(workbook.AsSpan(p)) & 3); p += 8;
                    string relId = WideString(workbook, ref p, end), name = WideString(workbook, ref p, end);
                    sheets.Add((name, state, rels.GetValueOrDefault(relId)));
                }
            }
            string? Target(string ending) => rels.Values.FirstOrDefault(t => t.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
            if (Target("sharedStrings.bin") is { } sst)
            {
                byte[] data = Part(sst);
                foreach (var (type, at, length) in Records(data))
                    if (type == 0x0013 && length >= 5 && strings.Count < 10_000_000) { int p = at + 1; strings.Add(WideString(data, ref p, at + length)); }   // BrtSSTItem
            }
            if (Target("styles.bin") is { } stylesPart) ReadStyles(Part(stylesPart));

            var view = new DocumentView { Kind = "sheet", Encoding = Label, CellStyles = styles.Table };
            int hidden = 0;
            var extra = new List<string> { "Fonts, fills, borders, pictures and charts in .xlsb workbooks are not shown in this version." };
            foreach (var (name, state, part) in sheets)
            {
                if (state != 0) { hidden++; continue; }
                if (part is null || !part.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || !part.Contains("worksheets/", StringComparison.OrdinalIgnoreCase))
                { extra.Add($"The sheet \"{name}\" is not a worksheet and is not shown in this version."); continue; }
                view.Sheets.Add(ReadSheet(name, Part(part), view.Sheets.Count));
            }
            if (view.Sheets.Count == 0) throw new DocumentException("This workbook has no visible worksheets to show.");
            view.Notice = Notes(hidden, truncated, stored, extra);
            return view;
        }

        private void ReadStyles(byte[] data)
        {
            bool cellXfs = false;
            foreach (var (type, at, length) in Records(data))
            {
                int end = at + length;
                switch (type)
                {
                    case 0x002C when length >= 6: { int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); int p = at + 2; custom[id] = WideString(data, ref p, end); break; }   // BrtFmt
                    case 0x0269: cellXfs = true; break;                                     // BrtBeginCellXFs
                    case 0x026A: cellXfs = false; break;
                    case 0x002F when cellXfs && length >= 14 && xfs.Count < 65_536:         // BrtXF: format at 2, alignment in the flags at 12
                        {
                            int format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
                            int align = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12)) & 0x07;
                            xfs.Add((format, align switch { 1 => 'l', 2 => 'c', 3 => 'r', 5 => 'l', 6 => 'c', 7 => 'l', _ => '\0' }));
                            break;
                        }
                }
            }
        }

        private SheetData ReadSheet(string name, byte[] data, int index)
        {
            using var sheet = new SheetBuilder(name, index, storeFolder, styles);
            int row = -1;
            foreach (var (type, at, length) in Records(data))
            {
                int end = at + length;
                switch (type)
                {
                    case 0x0000 when length >= 4: row = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)); break;   // BrtRowHdr
                    case >= 0x0001 and <= 0x000B when length >= 8 && row >= 0:                                                // cells
                        {
                            int column = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                            int xf = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4)) & 0xFFFFFF);
                            if (column < 0 || column >= Spreadsheets.MaxColumns) break;
                            int p = at + 8;
                            switch (type)
                            {
                                case 0x0002 when length >= 12: Number(Rk(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)))); break;
                                case 0x0005 or 0x0009 when length >= 16: Number(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(p))); break;
                                case 0x0006 or 0x0008: Text(WideString(data, ref p, end)); break;
                                case 0x0007 when length >= 12: { int i = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); Text(i >= 0 && i < strings.Count ? strings[i] : ""); break; }
                                case 0x0004 or 0x000A when length >= 9: Cell(data[p] != 0 ? "TRUE" : "FALSE", 'c'); break;
                                case 0x0003 or 0x000B when length >= 9: Cell(ErrorText(data[p]), 'c', null, true); break;
                            }
                            void Number(double value)
                            {
                                if (!double.IsFinite(value)) { Cell("#NUM!", 'c', null, true); return; }
                                int format = xf < xfs.Count ? xfs[xf].Format : 0;
                                Cell(Spreadsheets.FormatValue(value, format, custom, culture, date1904, cache), 'r', value);
                            }
                            void Text(string text) => Cell(text, 'l');
                            void Cell(string text, char natural, double? value = null, bool error = false)
                            {
                                char align = xf < xfs.Count && xfs[xf].Align != '\0' ? xfs[xf].Align : natural;
                                sheet.Add(row, column, text, align, 0, ref budget);
                                sheet.Value(row, column, value, error);
                            }
                            break;
                        }
                    case 0x003C when length >= 18:                                           // BrtColInfo
                        {
                            int first = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), last = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
                            double width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 8)) / 256.0;
                            bool isHidden = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 16)) & 1) != 0;
                            for (int c = Math.Max(0, first); c <= Math.Min(last, Spreadsheets.MaxColumns - 1); c++) sheet.Widths[c] = isHidden ? 0 : width;
                            break;
                        }
                    case 0x00B0 when length >= 16 && sheet.Sheet.Merges.Count < 100_000:          // BrtMergeCell
                        sheet.Sheet.Merges.Add([BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 8)),
                            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 12))]);
                        break;
                    case 0x0097 when length >= 29 && (data[at + 28] & 1) != 0:                    // BrtPane, frozen
                        sheet.Sheet.FrozenColumns = (int)Math.Clamp(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)), 0, Spreadsheets.MaxColumns);
                        sheet.Sheet.FrozenRows = (int)Math.Clamp(BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at + 8)), 0, Spreadsheets.MaxRowsPerSheet);
                        break;
                }
            }
            var built = sheet.Build();
            truncated |= sheet.Truncated; stored |= built.Store.Length > 0;
            return built;
        }

        // An RK number: a 30-bit integer or the top of a double, possibly divided by 100.
        private static double Rk(uint rk)
        {
            double value = (rk & 2) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
            return (rk & 1) != 0 ? value / 100 : value;
        }

        private static string ErrorText(byte code) => code switch
        {
            0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", _ => "#GETTING_DATA"
        };
    }
}
