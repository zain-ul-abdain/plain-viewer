using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace PlainViewer.Core;

// Conditional formatting of .xls and .ods sheets, read into the same rules as .xlsx (ConditionalFormats) and applied to
// the saved values after the sheet is read.
public static partial class LegacySpreadsheets
{
    // ---- Excel 97-2003: CONDFMT and CF records ----

    private sealed partial class Excel97
    {
        // CONDFMT: the ranges the CF records after it apply to (SqRefU after ccf, flags and the bounding range).
        private List<int[]> ConditionRanges(int at, int length)
        {
            var ranges = new List<int[]>();
            if (length < 14) return ranges;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 12));
            for (int k = 0; k < count && k < 100 && 14 + k * 8 + 8 <= length; k++)
            {
                var span = data.AsSpan(at + 14 + k * 8);
                int row1 = BinaryPrimitives.ReadUInt16LittleEndian(span), row2 = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                int column1 = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]), column2 = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
                if (row1 <= row2 && column1 <= column2) ranges.Add([row1, column1, row2, column2]);
            }
            return ranges;
        }

        // CF: one rule of the CONDFMT before it, with its format (DXFN) and one or two parsed formulas. Excel 97-2003 rules
        // compare the cell's value (shown when the formulas are constants) or are formulas (never evaluated). In a
        // CONDFMT the first true rule wins, so every rule stops the ones after it.
        private void ReadCondition(int at, int length, List<int[]> ranges, int priority, ConditionalFormats target)
        {
            int end = at + length;
            if (length < 12 || ranges.Count == 0) { target.NotShown++; return; }
            int kind = data[at], comparison = data[at + 1];
            int cce1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)), cce2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 6));
            int more = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 10));
            bool Has(int bit) => (flags & (1u << bit)) != 0;
            int p = at + 12;
            var format = new WorkbookStyles.Dxf();
            if (Has(25)) p += (more & 1) != 0 && p + 2 <= end ? Math.Max(2, (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p))) : 2;  // number format: not shown
            if (Has(26))
            {
                // DXFFntD: Stxp at 64 (height, ts, bls, sss, uls), then icvFore at 80 and the "not changed" flags.
                if (p + 118 > end) { target.NotShown++; return; }
                uint ts = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 68)), tsNinch = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 88));
                int bls = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 72));
                uint colour = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 80));
                if ((tsNinch & 0x02) == 0) format.Italic = (ts & 0x02) != 0;
                if ((tsNinch & 0x80) == 0) format.Strike = (ts & 0x80) != 0;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 96)) == 0) format.Underline = data[p + 76] != 0;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 100)) == 0) format.Bold = bls >= 600;
                if (colour < 64) format.Color = Colour((int)colour);
                p += 118;
            }
            if (Has(27)) p += 8;                                                      // alignment: not changed by rules
            if (Has(28))
            {
                // DXFBdr: the XF record's border layout; a side is set only when its "not changed" flag is clear.
                if (p + 8 > end) { target.NotShown++; return; }
                uint lines = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)), colours = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4));
                if (!Has(10)) format.Left = Border((int)(lines & 0xF), Colour((int)((lines >> 16) & 0x7F)));
                if (!Has(11)) format.Right = Border((int)((lines >> 4) & 0xF), Colour((int)((lines >> 23) & 0x7F)));
                if (!Has(12)) format.Top = Border((int)((lines >> 8) & 0xF), Colour((int)(colours & 0x7F)));
                if (!Has(13)) format.Bottom = Border((int)((lines >> 12) & 0xF), Colour((int)((colours >> 7) & 0x7F)));
                p += 8;
            }
            if (Has(29))
            {
                // DXFPat: a solid fill's colour is the background colour (as in an .xlsx dxf).
                if (p + 4 > end) { target.NotShown++; return; }
                uint pattern = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
                int fill = (int)((pattern >> 10) & 0x3F), fore = (int)((pattern >> 16) & 0x7F), back = (int)((pattern >> 23) & 0x7F);
                if (Has(16) || fill == 1) format.Fill = !Has(18) ? Colour(back) : !Has(17) ? Colour(fore) : null;
                p += 4;
            }
            if (Has(30)) p += 2;                                                      // protection
            // Each formula as a constant, or as formula text (worked out per cell by ConditionFormula).
            int originRow = ranges[0][0], originColumn = ranges[0][1];
            string? first = cce1 > 0 && p + cce1 <= end ? Constant(p, cce1) ?? FormulaText(p, cce1, originRow, originColumn) : null;
            string? second = cce2 > 0 && p + cce1 + cce2 <= end ? Constant(p + cce1, cce2) ?? FormulaText(p + cce1, cce2, originRow, originColumn) : null;
            if (kind == 2)
            {
                // A formula rule: the format applies where the formula is true.
                if (first is null) { target.NotShown++; return; }
                var formulaRule = new ConditionalFormats.Rule { Type = "expression", Format = format, Priority = priority, Stop = true };
                formulaRule.Formulas.Add(first);
                formulaRule.Ranges.AddRange(ranges);
                target.Add(formulaRule);
                return;
            }
            string? operation = comparison switch
            {
                1 => "between", 2 => "notBetween", 3 => "equal", 4 => "notEqual",
                5 => "greaterThan", 6 => "lessThan", 7 => "greaterThanOrEqual", 8 => "lessThanOrEqual", _ => null
            };
            bool pair = comparison is 1 or 2;
            if (kind != 1 || operation is null || first is null || pair && second is null) { target.NotShown++; return; }
            var rule = new ConditionalFormats.Rule { Type = "cellIs", Operator = operation, Format = format, Priority = priority, Stop = true };
            rule.Formulas.Add(first);
            if (pair) rule.Formulas.Add(second!);
            rule.Ranges.AddRange(ranges);
            target.Add(rule);
        }

        // A parsed formula ([MS-XLS] 2.5.198, reverse Polish tokens) written out as formula text for ConditionFormula:
        // constants, operators, parentheses, references and areas on this sheet (ptgRef/ptgArea, and the relative
        // ptgRefN/ptgAreaN, whose offsets count from the rule's first cell) and the functions in FunctionNames. Null for
        // anything else, so the rule stays counted as not shown.
        private string? FormulaText(int at, int length, int originRow, int originColumn)
        {
            var stack = new Stack<string>();
            int p = at, end = at + length;
            static string Column(int c) { var s = ""; for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s; return s; }
            string Cell(int row, int column, bool rowRelative, bool columnRelative) =>
                (columnRelative ? "" : "$") + Column(column) + (rowRelative ? "" : "$") + (row + 1).ToString(CultureInfo.InvariantCulture);
            (int Row, int Column, bool RowRel, bool ColRel)? Reference(int row, int field, bool offsets)
            {
                bool rowRelative = (field & 0x8000) != 0, columnRelative = (field & 0x4000) != 0;
                int column = field & 0x3FFF;
                if (offsets)
                {
                    if (rowRelative) row = originRow + (short)row;
                    if (columnRelative) column = originColumn + (sbyte)(column & 0xFF);
                }
                return row < 0 || column < 0 || row > 1_048_575 || column > 16_383 ? null : (row, column, rowRelative, columnRelative);
            }
            try
            {
                while (p < end)
                {
                    int token = data[p++];
                    int baseToken = token >= 0x20 ? (token & 0x1F) | 0x20 : token;       // the reference class does not matter here
                    switch (baseToken)
                    {
                        case 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x0E:
                            {
                                string right = stack.Pop(), left = stack.Pop();
                                string op = token switch { 0x03 => "+", 0x04 => "-", 0x05 => "*", 0x06 => "/", 0x07 => "^", 0x08 => "&", 0x09 => "<", 0x0A => "<=", 0x0B => "=", 0x0C => ">=", 0x0D => ">", _ => "<>" };
                                stack.Push(left + op + right);
                                break;
                            }
                        case 0x12: break;                                                   // unary plus
                        case 0x13: stack.Push("-" + stack.Pop()); break;
                        case 0x14: stack.Push(stack.Pop() + "%"); break;
                        case 0x15: stack.Push("(" + stack.Pop() + ")"); break;
                        case 0x16: stack.Push(""); break;                                   // missing argument
                        case 0x17:
                            {
                                int count = data[p]; bool wide = (data[p + 1] & 1) != 0;
                                string text = wide ? Encoding.Unicode.GetString(data, p + 2, count * 2) : Encoding.Latin1.GetString(data, p + 2, count);
                                p += 2 + count * (wide ? 2 : 1);
                                stack.Push("\"" + text.Replace("\"", "\"\"") + "\"");
                                break;
                            }
                        case 0x19:
                            {
                                int attributes = data[p]; int data2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 1)); p += 3;
                                if ((attributes & 0x04) != 0) p += 2 * (data2 + 1);                // choose: jump table
                                if ((attributes & 0x10) != 0) stack.Push("SUM(" + stack.Pop() + ")");
                                break;                                                       // spaces, if and goto: layout only
                            }
                        case 0x1D: stack.Push(data[p++] != 0 ? "TRUE" : "FALSE"); break;
                        case 0x1E: stack.Push(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)).ToString(CultureInfo.InvariantCulture)); p += 2; break;
                        case 0x1F:
                            {
                                double n = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(p)); p += 8;
                                if (!double.IsFinite(n)) return null;
                                stack.Push(n.ToString("R", CultureInfo.InvariantCulture));
                                break;
                            }
                        case 0x21 or 0x22:
                            {
                                int argc, id;
                                if (baseToken == 0x22) { argc = data[p] & 0x7F; id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 1)) & 0x7FFF; p += 3; }
                                else { id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)); p += 2; argc = -1; }
                                if (!FunctionNames.TryGetValue(id, out var function)) return null;
                                if (argc < 0) argc = function.Arguments;
                                if (argc < 0 || argc > stack.Count) return null;
                                var arguments = new string[argc];
                                for (int i = argc - 1; i >= 0; i--) arguments[i] = stack.Pop();
                                stack.Push(function.Name + "(" + string.Join(",", arguments) + ")");
                                break;
                            }
                        case 0x24 or 0x2C:
                            {
                                var cell = Reference(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2)), baseToken == 0x2C);
                                p += 4;
                                if (cell is not { } c) return null;
                                stack.Push(Cell(c.Row, c.Column, c.RowRel, c.ColRel));
                                break;
                            }
                        case 0x25 or 0x2D:
                            {
                                int r1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)), r2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 2));
                                int f1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4)), f2 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 6));
                                p += 8;
                                if (Reference(r1, f1, baseToken == 0x2D) is not { } a || Reference(r2, f2, baseToken == 0x2D) is not { } b) return null;
                                stack.Push(Cell(a.Row, a.Column, a.RowRel, a.ColRel) + ":" + Cell(b.Row, b.Column, b.RowRel, b.ColRel));
                                break;
                            }
                        default: return null;                                                // names, other sheets, arrays, errors…
                    }
                }
            }
            catch (InvalidOperationException) { return null; }                              // a malformed token list
            catch (ArgumentException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
            return stack.Count == 1 ? stack.Pop() : null;
        }

        // Built-in functions by their number ([MS-XLS] 2.5.198.17 Ftab) and fixed argument count (-1: variable).
        private static readonly Dictionary<int, (string Name, int Arguments)> FunctionNames = new()
        {
            [0] = ("COUNT", -1), [1] = ("IF", -1), [2] = ("ISNA", 1), [3] = ("ISERROR", 1), [4] = ("SUM", -1), [5] = ("AVERAGE", -1), [6] = ("MIN", -1),
            [7] = ("MAX", -1), [8] = ("ROW", -1), [9] = ("COLUMN", -1), [24] = ("ABS", 1), [25] = ("INT", 1), [27] = ("ROUND", 2), [31] = ("MID", 3),
            [32] = ("LEN", 1), [33] = ("VALUE", 1), [34] = ("TRUE", 0), [35] = ("FALSE", 0), [36] = ("AND", -1), [37] = ("OR", -1), [38] = ("NOT", 1),
            [39] = ("MOD", 2), [82] = ("SEARCH", -1), [112] = ("LOWER", 1), [113] = ("UPPER", 1), [115] = ("LEFT", -1), [116] = ("RIGHT", -1),
            [117] = ("EXACT", 2), [118] = ("TRIM", 1), [124] = ("FIND", -1), [126] = ("ISERR", 1), [127] = ("ISTEXT", 1), [128] = ("ISNUMBER", 1),
            [129] = ("ISBLANK", 1), [169] = ("COUNTA", -1), [190] = ("ISNONTEXT", 1), [198] = ("ISLOGICAL", 1), [212] = ("ROUNDUP", 2),
            [213] = ("ROUNDDOWN", 2), [346] = ("COUNTIF", 2), [347] = ("COUNTBLANK", 1),
        };

        // A parsed formula that is a single constant (a number, possibly negated, text or TRUE/FALSE) as the rules'
        // constant form; null for anything to calculate.
        private string? Constant(int at, int length)
        {
            var tokens = data.AsSpan(at, length);
            bool negate = tokens.Length > 1 && tokens[^1] == 0x13;                     // tUminus after the number
            if (negate) tokens = tokens[..^1];
            double? number = tokens[0] switch
            {
                0x1E when tokens.Length == 3 => BinaryPrimitives.ReadUInt16LittleEndian(tokens[1..]),
                0x1F when tokens.Length == 9 => BinaryPrimitives.ReadDoubleLittleEndian(tokens[1..]),
                _ => null
            };
            if (number is double n) return double.IsFinite(n) ? (negate ? -n : n).ToString("R", CultureInfo.InvariantCulture) : null;
            if (negate) return null;
            if (tokens[0] == 0x1D && tokens.Length == 2) return tokens[1] != 0 ? "TRUE" : "FALSE";
            if (tokens[0] == 0x17 && tokens.Length >= 3)
            {
                int count = tokens[1];
                bool wide = (tokens[2] & 0x01) != 0;
                if (tokens.Length != 3 + count * (wide ? 2 : 1)) return null;
                string text = wide ? Encoding.Unicode.GetString(tokens.Slice(3, count * 2)) : Encoding.Latin1.GetString(tokens.Slice(3, count));
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return null;
        }
    }

    // ---- OpenDocument: calcext:conditional-formats (LibreOffice) ----

    private const string CalcExtNs = "urn:org:documentfoundation:names:experimental:calc:xmlns:calcext:1.0";
    private static string? Ext(XElement e, string name) => e.Attribute(XName.Get(name, CalcExtNs))?.Value;

    // A sheet's conditional formats: conditions (with a cell style), colour scales, data bars and icon sets. Conditions
    // written as formulas and date conditions are counted as not shown.
    private static void ReadConditions(XElement formats, ConditionalFormats target, Func<string, WorkbookStyles.Dxf?> style)
    {
        int priority = 0;
        foreach (var format in formats.Elements(XName.Get("conditional-format", CalcExtNs)).Take(1000))
        {
            var ranges = OpenDocumentRanges(Ext(format, "target-range-address") ?? "");
            foreach (var e in format.Elements().Take(200))
            {
                priority++;
                var rule = e.Name.NamespaceName != CalcExtNs ? null : e.Name.LocalName switch
                {
                    "condition" => Condition(e, style),
                    "color-scale" => Scale(e, "colorScale"),
                    "data-bar" => Scale(e, "dataBar"),
                    "icon-set" => Scale(e, "iconSet"),
                    _ => null                                                          // date-is and others
                };
                if (rule is null || ranges.Count == 0) { target.NotShown++; continue; }
                rule.Priority = priority;
                rule.Ranges.AddRange(ranges);
                target.Add(rule);
            }
        }
    }

    // "Sheet1.A2:Sheet1.A7 'My sheet'.C1" as zero-based [row1, column1, row2, column2] ranges.
    private static List<int[]> OpenDocumentRanges(string address)
    {
        var ranges = new List<int[]>();
        foreach (Match m in RangeAddress().Matches(address))
        {
            if (ranges.Count >= 100) break;
            if (!Spreadsheets.TryCell(m.Groups[1].Value + m.Groups[2].Value, out int row1, out int column1)) continue;
            int row2 = row1, column2 = column1;
            if (m.Groups[3].Success && !Spreadsheets.TryCell(m.Groups[3].Value + m.Groups[4].Value, out row2, out column2)) continue;
            ranges.Add([Math.Min(row1, row2) - 1, Math.Min(column1, column2), Math.Max(row1, row2) - 1, Math.Max(column1, column2)]);
        }
        return ranges;
    }
    [GeneratedRegex(@"(?:'(?:[^']|'')*'|[^\s.:']*)\.\$?([A-Za-z]{1,3})\$?(\d{1,7})(?::(?:'(?:[^']|'')*'|[^\s.:']*)\.\$?([A-Za-z]{1,3})\$?(\d{1,7}))?")]
    private static partial Regex RangeAddress();

    // A condition's formula in LibreOffice's notation ("[.F2]>2", "AND([.$A1]>1;[.B1:.B5]...)") as formula text for
    // ConditionFormula: references in brackets lose them and their sheet-less dot, ";" between arguments becomes ",".
    // Null for references to other sheets. Quoted text is left as it is.
    private static string? OpenFormula(string formula)
    {
        var result = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < formula.Length; i++)
        {
            char c = formula[i];
            if (c == '"') { quoted = !quoted; result.Append(c); continue; }
            if (quoted) { result.Append(c); continue; }
            if (c == '[')
            {
                int close = formula.IndexOf(']', i);
                if (close < 0) return null;
                string inside = formula[(i + 1)..close];
                var parts = inside.Split(':');
                if (parts.Length > 2 || parts.Any(p => !p.StartsWith('.') || p.Length < 3)) return null;      // another sheet, or not a reference
                result.Append(string.Join(":", parts.Select(p => p[1..])));
                i = close;
                continue;
            }
            result.Append(c == ';' ? ',' : c);
        }
        return quoted ? null : result.ToString();
    }

    // calcext:value of a condition: a comparison ("&gt;35", "between(1,2)") or a named test ("contains-text(\"x\")").
    private static ConditionalFormats.Rule? Condition(XElement e, Func<string, WorkbookStyles.Dxf?> style)
    {
        string value = (Ext(e, "value") ?? "").Trim();
        var rule = new ConditionalFormats.Rule { Format = style(Ext(e, "apply-style-name") ?? "") };
        // Formulas are relative to the condition's base cell ("Rules.F2").
        if (OpenDocumentRanges(Ext(e, "base-cell-address") ?? "") is [var origin, ..]) rule.Origin = [origin[0], origin[1]];
        foreach (var (prefix, operation) in new[] { ("<=", "lessThanOrEqual"), (">=", "greaterThanOrEqual"), ("!=", "notEqual"), ("<", "lessThan"), (">", "greaterThan"), ("=", "equal") })
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (OpenFormula(value[prefix.Length..].Trim()) is not { } operand) return null;
                rule.Type = "cellIs"; rule.Operator = operation;
                rule.Formulas.Add(operand);
                return rule;
            }
        if (value.StartsWith("formula-is(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            if (OpenFormula(value["formula-is(".Length..^1]) is not { } formula) return null;
            rule.Type = "expression";
            rule.Formulas.Add(formula);
            return rule;
        }
        int open = value.IndexOf('(');
        string name = open < 0 ? value : value[..open];
        var args = open < 0 || !value.EndsWith(')') ? [] : Arguments(value[(open + 1)..^1]);
        int Rank() => args.Count == 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 1000) : -1;
        switch (name)
        {
            case "between" or "not-between" when args.Count == 2:
                if (OpenFormula(args[0]) is not { } low || OpenFormula(args[1]) is not { } high) return null;
                rule.Type = "cellIs"; rule.Operator = name == "between" ? "between" : "notBetween"; rule.Formulas.Add(low); rule.Formulas.Add(high); return rule;
            case "duplicate": rule.Type = "duplicateValues"; return rule;
            case "unique": rule.Type = "uniqueValues"; return rule;
            case "top-elements" or "bottom-elements" or "top-percent" or "bottom-percent" when Rank() > 0:
                rule.Type = "top10"; rule.Rank = Rank(); rule.Bottom = name.StartsWith("bottom", StringComparison.Ordinal); rule.Percent = name.EndsWith("percent", StringComparison.Ordinal);
                return rule;
            case "above-average" or "below-average" or "above-equal-average" or "below-equal-average":
                rule.Type = "aboveAverage"; rule.Above = name.StartsWith("above", StringComparison.Ordinal); rule.EqualAverage = name.Contains("equal", StringComparison.Ordinal);
                return rule;
            case "is-error": rule.Type = "containsErrors"; return rule;
            case "is-no-error": rule.Type = "notContainsErrors"; return rule;
            case "begins-with" or "ends-with" or "contains-text" or "not-contains-text" when args.Count == 1 && Unquote(args[0]) is { Length: > 0 } text:
                rule.Type = name switch { "begins-with" => "beginsWith", "ends-with" => "endsWith", "contains-text" => "containsText", _ => "notContainsText" };
                rule.Text = text;
                return rule;
            default: return null;                                                  // formula-is and others
        }
    }

    // Arguments separated by commas or semicolons outside quoted text.
    private static List<string> Arguments(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && c is ',' or ';') { args.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0 || args.Count > 0) args.Add(current.ToString().Trim());
        return args.Take(4).ToList();
    }

    private static string? Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Replace("\"\"", "").Contains('"') ? text[1..^1].Replace("\"\"", "\"") : null;

    // Colour scales, data bars and icon sets, with their entries as .xlsx scale points.
    private static ConditionalFormats.Rule? Scale(XElement e, string type)
    {
        var rule = new ConditionalFormats.Rule { Type = type };
        foreach (var entry in e.Elements().Where(c => c.Name.NamespaceName == CalcExtNs && c.Name.LocalName is "color-scale-entry" or "formatting-entry").Take(5))
        {
            string? kind = Ext(entry, "type") switch
            {
                "minimum" => "min", "maximum" => "max", "auto-minimum" => "autoMin", "auto-maximum" => "autoMax",
                "percent" => "percent", "percentile" => "percentile", "number" => "num", _ => null      // formula: not evaluated
            };
            if (kind is null) return null;
            rule.Points.Add((kind, Ext(entry, "value") ?? "0", true));
            if (type == "colorScale") rule.Colours.Add(WorkbookStyles.Hex(Ext(entry, "color")?.TrimStart('#')));
        }
        static int Length(string? text, int missing) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 0, 100) : missing;
        if (type == "dataBar")
        {
            rule.Colours.Add(WorkbookStyles.Hex(Ext(e, "positive-color")?.TrimStart('#')));
            rule.MinLength = Length(Ext(e, "min-length"), 10);
            rule.MaxLength = Math.Max(rule.MinLength, Length(Ext(e, "max-length"), 90));
        }
        if (type == "iconSet") rule.IconSet = Ext(e, "icon-set-type") ?? "";
        if (Ext(e, "show-value") == "false") rule.ShowValue = false;
        return rule;
    }
}
