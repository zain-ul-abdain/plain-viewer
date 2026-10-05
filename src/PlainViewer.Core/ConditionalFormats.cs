using System.Globalization;
using System.Xml.Linq;
namespace PlainViewer.Core;

// Conditional formatting of an .xlsx worksheet, worked out from the values saved in the cells: cell formulas are never
// recalculated. Rules written as formulas ("expression" rules, and value rules that compare with a formula) are worked
// out by ConditionFormula over the saved values; rules it cannot work out and rules that depend on today's date
// ("dates occurring") are counted and not shown. Shown: value comparisons with constants or formulas, text contains/begins/ends, blanks and errors, top and bottom (count or percent), above and below average,
// duplicate and unique values, colour scales, data bars and icon sets. Rules apply in priority order; a property set by
// a rule with higher priority is kept, and "stop if true" ends the rules for that cell. The results become ordinary
// cell styles (WorkbookStyles.Intern), so the grid page only draws checked styles. The .xls and .ods readers add the
// same kinds of rules (Add) from their own records, so every format is evaluated here.
internal sealed class ConditionalFormats(WorkbookStyles? workbook = null)
{
    private const int MaxCellsPerRule = 250_000, MaxRules = 2_000;

    // One rule, in .xlsx terms: Type and Operator as in a cfRule, Formulas as constants ("35", "\"text\""), scale points
    // as cfvo types and values.
    public sealed class Rule
    {
        public string Type = "", Operator = "", Text = "";
        public WorkbookStyles.Dxf? Format;
        public int Priority, Rank = 10, StdDev, MinLength = 10, MaxLength = 90;
        public bool Stop, Percent, Bottom, Above = true, EqualAverage, Reverse, ShowValue = true;
        public readonly List<string> Formulas = [];
        public readonly List<(string Type, string Value, bool Gte)> Points = [];
        public readonly List<string?> Colours = [];
        public string IconSet = "3TrafficLights1";
        public string? Extension;                          // id of the rule's Excel 2010 copy (data bar lengths)
        public int[]? Origin;                              // the cell formulas are relative to (default: the first range's top left)
        public int? Exclusive;                             // .ods: rules of one group, of which only the first true one applies to a cell
        public readonly List<int[]> Ranges = [];
    }

    private sealed class Overlay
    {
        public bool? Bold, Italic, Underline, Strike;
        public string? Color, Fill, Left, Right, Top, Bottom, Bar, Icon;
        public bool Stopped, HideText;
        public HashSet<int>? Matched;                      // exclusive groups that already applied to the cell
    }

    private readonly List<Rule> rules = [];
    private readonly Dictionary<string, (int Min, int Max)> barLengths = [];   // Excel 2010 data bars by id
    public int NotShown { get; set; }                     // rules that need formulas or today's date, or are not drawn
    public bool Any => rules.Count > 0;

    public const string LargeSheetNote = "Conditional formatting is not shown on sheets with more than 10,000 rows.";

    public void Add(Rule rule) { if (rules.Count < MaxRules) rules.Add(rule); }

    // The workbook notice for rules not shown.
    public static string? Note(int notShown) => notShown <= 0 ? null :
        $"{notShown} conditional formatting rule{(notShown == 1 ? " is" : "s are")} not shown: {(notShown == 1 ? "it is" : "they are")} written as formulas, depend on today's date or are of a kind this viewer does not draw, and this viewer never recalculates.";

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;
    private static int Int(XElement e, string name, int missing) => int.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : missing;
    private static bool Flag(XElement e, string name, bool missing) => Attr(e, name) is { } v ? v is "1" or "true" : missing;
    private static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(c => c.Name.LocalName == name);

    // One <conditionalFormatting sqref="..."> element with its rules. The Excel 2010 extension copies (x14) repeat rules
    // the main list already has, or use features not shown; they are counted as not shown unless they are data bars.
    public void Read(XElement element)
    {
        if (element.Name.NamespaceName.Contains("2009/9/main", StringComparison.Ordinal))
        {
            foreach (var copy in Children(element, "cfRule").Take(200))
            {
                if (Attr(copy, "type") != "dataBar") { NotShown++; continue; }
                if (Attr(copy, "id") is { } id && Children(copy, "dataBar").FirstOrDefault() is { } bar && barLengths.Count < 10_000)
                {
                    int min = Math.Clamp(Int(bar, "minLength", 10), 0, 100);
                    barLengths[id] = (min, Math.Clamp(Int(bar, "maxLength", 90), min, 100));
                }
            }
            return;
        }
        var ranges = new List<int[]>();
        foreach (var part in (Attr(element, "sqref") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(100))
        {
            if (Spreadsheets.TryRange(part, out var range)) ranges.Add(range);
            else if (Spreadsheets.TryCell(part, out int row, out int column)) ranges.Add([row - 1, column, row - 1, column]);
        }
        if (ranges.Count == 0) return;
        foreach (var e in Children(element, "cfRule").Take(200))
        {
            int dxf = Int(e, "dxfId", -1);
            var rule = new Rule
            {
                Type = Attr(e, "type") ?? "", Operator = Attr(e, "operator") ?? "", Text = Attr(e, "text") ?? "",
                Format = dxf >= 0 && dxf < workbook!.Dxfs.Count ? workbook.Dxfs[dxf] : null, Priority = Int(e, "priority", int.MaxValue), Stop = Flag(e, "stopIfTrue", false),
                Rank = Math.Clamp(Int(e, "rank", 10), 1, 1000), Percent = Flag(e, "percent", false), Bottom = Flag(e, "bottom", false),
                Above = Flag(e, "aboveAverage", true), EqualAverage = Flag(e, "equalAverage", false), StdDev = Math.Clamp(Int(e, "stdDev", 0), 0, 3)
            };
            rule.Extension = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "id")?.Value;
            rule.Ranges.AddRange(ranges);
            rule.Formulas.AddRange(Children(e, "formula").Select(f => f.Value).Take(2));
            var scale = Children(e, "colorScale").FirstOrDefault() ?? Children(e, "dataBar").FirstOrDefault() ?? Children(e, "iconSet").FirstOrDefault();
            if (scale is not null)
            {
                foreach (var point in Children(scale, "cfvo").Take(5)) rule.Points.Add((Attr(point, "type") ?? "", Attr(point, "val") ?? "", Flag(point, "gte", true)));
                foreach (var colour in Children(scale, "color").Take(3)) rule.Colours.Add(workbook!.Color(colour));
                rule.IconSet = Attr(scale, "iconSet") ?? rule.IconSet;
                rule.Reverse = Flag(scale, "reverse", false);
                rule.ShowValue = Flag(scale, "showValue", true);
                rule.MinLength = Math.Clamp(Int(scale, "minLength", 10), 0, 100);
                rule.MaxLength = Math.Clamp(Int(scale, "maxLength", 90), rule.MinLength, 100);
            }
            Add(rule);
        }
    }

    // Applies the rules to an .xlsx sheet's cells (rows held in memory): numbers and errors are the cells' saved values.
    public void Apply(SortedDictionary<int, List<(int Column, string Text, char Align, int Style)>> rows,
        IReadOnlyDictionary<long, double> numbers, IReadOnlySet<long> errors, int maxColumn)
    {
        if (rows.Count == 0) return;
        var positions = new Dictionary<long, (int Row, int Index)>();
        var cells = new Dictionary<long, (string Text, CellStyle Style)>();
        foreach (var (row, list) in rows)
            for (int i = 0; i < list.Count; i++)
            {
                long key = Key(row - 1, list[i].Column);
                positions[key] = (row, i); cells[key] = (list[i].Text, workbook!.Table[list[i].Style]);
            }
        foreach (var (row, column, style, hideText) in Evaluate(cells, numbers, errors, rows.Keys.Max() - 1, maxColumn))
        {
            int index = workbook!.Intern(style);
            if (index < 0) continue;
            if (positions.TryGetValue(Key(row, column), out var at))
            {
                var cell = rows[at.Row][at.Index];
                rows[at.Row][at.Index] = (cell.Column, hideText ? "" : cell.Text, cell.Align, index);
            }
            else
            {
                if (!rows.TryGetValue(row + 1, out var list)) rows[row + 1] = list = [];
                list.Add((column, "", 'l', index));
            }
        }
    }

    // The rules' results for a sheet's cells (Key -> text and style; maxRow zero-based, maxColumn a count): each changed
    // cell with its new style, and whether its value is hidden (bars or icons shown without the value). Empty cells are
    // listed only when the result is visible.
    public List<(int Row, int Column, CellStyle Style, bool HideText)> Evaluate(IReadOnlyDictionary<long, (string Text, CellStyle Style)> cells,
        IReadOnlyDictionary<long, double> numbers, IReadOnlySet<long> errors, int maxRow, int maxColumn)
    {
        var result = new List<(int, int, CellStyle, bool)>();
        if (rules.Count == 0 || maxRow < 0 || maxColumn == 0) return result;
        string Text(long key) => cells.TryGetValue(key, out var cell) ? cell.Text : "";
        double? Number(long key) => numbers.TryGetValue(key, out double v) ? v : null;
        foreach (var rule in rules)
            if (rule.Type == "dataBar" && rule.Extension is { } id && barLengths.TryGetValue(id, out var lengths)) (rule.MinLength, rule.MaxLength) = lengths;

        var overlays = new Dictionary<long, Overlay>();
        foreach (var rule in rules.OrderBy(r => r.Priority))
        {
            var targets = Cells(rule, maxRow, maxColumn).ToList();
            if (targets.Count == 0) continue;
            var values = targets.Select(Number).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            Func<long, Overlay, bool>? effect = Effect(rule, targets, values, Text, Number, errors);
            if (effect is null) { NotShown++; continue; }
            foreach (long key in targets)
            {
                if (overlays.TryGetValue(key, out var overlay) && (overlay.Stopped || rule.Exclusive is int taken && overlay.Matched?.Contains(taken) == true)) continue;
                overlay ??= new Overlay();
                if (!effect(key, overlay)) continue;
                overlays[key] = overlay;
                if (rule.Stop) overlay.Stopped = true;
                if (rule.Exclusive is int group) (overlay.Matched ??= []).Add(group);
            }
        }

        foreach (var (key, overlay) in overlays)
        {
            int row = (int)(key >> 16), column = (int)(key & 0xFFFF);
            bool present = cells.TryGetValue(key, out var cell);
            var merged = Merge(present ? cell.Style : new CellStyle(), overlay);
            if (present || merged.Visible) result.Add((row, column, merged, overlay.HideText));
        }
        return result;
    }
    public static long Key(int row, int column) => ((long)row << 16) | (uint)column;

    // The rule's cells, clipped to the sheet's data.
    private static IEnumerable<long> Cells(Rule rule, int maxRow, int maxColumn)
    {
        var seen = new HashSet<long>();
        foreach (var range in rule.Ranges)
            for (int r = range[0]; r <= Math.Min(range[2], maxRow); r++)
                for (int c = range[1]; c <= Math.Min(range[3], maxColumn - 1); c++)
                {
                    if (seen.Count >= MaxCellsPerRule) yield break;
                    long key = Key(r, c);
                    if (seen.Add(key)) yield return key;
                }
    }

    // What the rule does to one cell (true when it applies), or null when it cannot be shown without formulas.
    private Func<long, Overlay, bool>? Effect(Rule rule, List<long> cells, List<double> values, Func<long, string> text,
        Func<long, double?> number, IReadOnlySet<long> errors)
    {
        var dxf = rule.Format;
        Func<long, Overlay, bool> When(Func<long, bool> test) => (key, overlay) => { if (!test(key)) return false; ApplyDxf(dxf, overlay); return true; };
        bool Blank(long key) => text(key).Trim().Length == 0;
        // A cell's saved value for formulas: an error, a number, TRUE/FALSE, text, or null when empty.
        object? CellValue(int row, int column)
        {
            long key = Key(row, column);
            if (errors.Contains(key)) return ConditionFormula.Error.Value;
            if (number(key) is double n) return n;
            string t = text(key);
            return t.Length == 0 ? null : t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? true : t.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? false : t;
        }
        int originRow = rule.Origin?[0] ?? (rule.Ranges.Count > 0 ? rule.Ranges[0][0] : 0), originColumn = rule.Origin?[1] ?? (rule.Ranges.Count > 0 ? rule.Ranges[0][1] : 0);
        int RowOf(long key) => (int)(key >> 16);
        int ColumnOf(long key) => (int)(key & 0xFFFF);
        // A formula whose moving ranges would make it read more than 20 million cells over the rule's cells (for
        // example =SUM(A1:J10000)>0 over 250,000 cells) is not worked out: the rule stays counted as not shown, and
        // opening the file stays within the worker's time limit.
        ConditionFormula? Affordable(string source) => ConditionFormula.Parse(source) is { } f && f.CellsPerCell * cells.Count <= 20_000_000 ? f : null;
        if (rule.Type is "containsText" or "notContainsText" or "beginsWith" or "endsWith" && rule.Text.Length == 0) return null;
        switch (rule.Type)
        {
            case "expression":
                {
                    if (rule.Formulas.Count == 0 || Affordable(rule.Formulas[0]) is not { } formula) return null;
                    return When(key => formula.IsTrue(RowOf(key), ColumnOf(key), originRow, originColumn, CellValue));
                }
            case "cellIs":
                {
                    // Each operand: a constant, or a formula worked out for the cell (relative to the rule's first cell).
                    var operands = new List<Func<long, (double? Number, string? Text)>>();
                    foreach (var source in rule.Formulas)
                    {
                        if (Constant(source) is { } constant) { operands.Add(_ => constant); continue; }
                        if (Affordable(source) is not { } formula) return null;
                        operands.Add(key => formula.Evaluate(RowOf(key), ColumnOf(key), originRow, originColumn, CellValue) switch
                        {
                            double d => (d, null), string s => (null, s), bool flag => (flag ? 1 : 0, null), null => (0, null), _ => (double.NaN, null)
                        });
                    }
                    if (operands.Count == 0) return null;
                    var first = operands[0]; var second = operands.Count > 1 ? operands[1] : first;
                    return When(key =>
                    {
                        var a = first(key); var b = second(key);
                        if (a.Number is double.NaN || b.Number is double.NaN) return false;      // the formula gave an error
                        int ca = Compare(key, a), cb = Compare(key, b);
                        return rule.Operator switch
                        {
                            // Between the two values in either order: at or above one and at or below the other.
                            "between" => ca >= 0 && cb <= 0 || ca <= 0 && cb >= 0,
                            "notBetween" => !(ca >= 0 && cb <= 0 || ca <= 0 && cb >= 0),
                            "equal" => ca == 0, "notEqual" => ca != 0,
                            "greaterThan" => ca > 0, "lessThan" => ca < 0, "greaterThanOrEqual" => ca >= 0, "lessThanOrEqual" => ca <= 0,
                            _ => false
                        };
                    });
                    // Excel's comparison: an empty cell counts as 0 against numbers and as "" against text; numbers sort before text.
                    int Compare(long key, (double? Number, string? Text) constant)
                    {
                        double? n = number(key); string t = text(key);
                        if (constant.Number is double c) return n is double v ? v.CompareTo(c) : t.Length == 0 ? 0.0.CompareTo(c) : 1;
                        if (n is not null) return -1;
                        return string.Compare(t, constant.Text, StringComparison.OrdinalIgnoreCase) switch { < 0 => -1, > 0 => 1, _ => 0 };
                    }
                }
            case "containsText": return When(key => !Blank(key) && text(key).Contains(rule.Text, StringComparison.OrdinalIgnoreCase));
            case "notContainsText": return When(key => !text(key).Contains(rule.Text, StringComparison.OrdinalIgnoreCase));
            case "beginsWith": return When(key => text(key).StartsWith(rule.Text, StringComparison.OrdinalIgnoreCase));
            case "endsWith": return When(key => text(key).EndsWith(rule.Text, StringComparison.OrdinalIgnoreCase));
            case "containsBlanks": return When(Blank);
            case "notContainsBlanks": return When(key => !Blank(key));
            case "containsErrors": return When(errors.Contains);
            case "notContainsErrors": return When(key => !errors.Contains(key));
            case "top10":
                {
                    if (values.Count == 0) return When(_ => false);
                    var sorted = rule.Bottom ? values.OrderBy(v => v).ToList() : values.OrderByDescending(v => v).ToList();
                    int count = rule.Percent ? Math.Max(1, (int)Math.Floor(values.Count * rule.Rank / 100.0)) : rule.Rank;
                    double limit = sorted[Math.Min(count, sorted.Count) - 1];
                    return When(key => number(key) is double v && (rule.Bottom ? v <= limit : v >= limit));
                }
            case "aboveAverage":
                {
                    if (values.Count == 0) return When(_ => false);
                    double mean = values.Average(), deviation = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
                    double limit = rule.Above ? mean + rule.StdDev * deviation : mean - rule.StdDev * deviation;
                    return When(key => number(key) is double v && (rule.Above ? v > limit || rule.EqualAverage && v == limit : v < limit || rule.EqualAverage && v == limit));
                }
            case "duplicateValues" or "uniqueValues":
                {
                    string? Value(long key) => number(key) is double v ? "n" + v.ToString("R", CultureInfo.InvariantCulture) : Blank(key) ? null : "t" + text(key).ToLowerInvariant();
                    var counts = cells.Select(Value).Where(v => v is not null).GroupBy(v => v!).ToDictionary(g => g.Key, g => g.Count());
                    bool duplicates = rule.Type == "duplicateValues";
                    return When(key => Value(key) is { } v && (counts[v] > 1) == duplicates);
                }
            case "colorScale":
                {
                    if (rule.Points.Count is < 2 or > 3 || rule.Colours.Count != rule.Points.Count || rule.Colours.Any(c => c is null)) return null;
                    var limits = rule.Points.Select(p => Threshold(p.Type, p.Value, values)).ToList();
                    if (limits.Any(l => l is null)) return null;
                    return (key, overlay) =>
                    {
                        if (number(key) is not double v) return false;
                        overlay.Fill ??= Scale(v, limits.Select(l => l!.Value).ToList(), rule.Colours!);
                        return true;
                    };
                }
            case "dataBar":
                {
                    if (rule.Points.Count != 2 || rule.Colours.FirstOrDefault() is not { } colour) return null;
                    double? low = Threshold(rule.Points[0].Type, rule.Points[0].Value, values), high = Threshold(rule.Points[1].Type, rule.Points[1].Value, values);
                    if (low is null || high is null) return null;
                    return (key, overlay) =>
                    {
                        if (number(key) is not double v) return false;
                        double share = high > low ? Math.Clamp((v - low.Value) / (high.Value - low.Value), 0, 1) : 1;
                        overlay.Bar ??= $"{Math.Round(rule.MinLength + share * (rule.MaxLength - rule.MinLength))} {colour}";
                        if (!rule.ShowValue) overlay.HideText = true;
                        return true;
                    };
                }
            case "iconSet":
                {
                    var icons = Icons(rule.IconSet);
                    if (icons is null || rule.Points.Count != icons.Length) return null;
                    var limits = rule.Points.Select(p => Threshold(p.Type, p.Value, values)).ToList();
                    if (limits.Any(l => l is null)) return null;
                    return (key, overlay) =>
                    {
                        if (number(key) is not double v) return false;
                        int index = 0;
                        for (int i = 1; i < limits.Count; i++) if (rule.Points[i].Gte ? v >= limits[i] : v > limits[i]) index = i;
                        overlay.Icon ??= icons[rule.Reverse ? icons.Length - 1 - index : index];
                        if (!rule.ShowValue) overlay.HideText = true;
                        return true;
                    };
                }
            default: return null;                                   // expression, timePeriod and others
        }
    }

    private static void ApplyDxf(WorkbookStyles.Dxf? dxf, Overlay overlay)
    {
        if (dxf is null) return;
        overlay.Bold ??= dxf.Bold; overlay.Italic ??= dxf.Italic; overlay.Underline ??= dxf.Underline; overlay.Strike ??= dxf.Strike;
        overlay.Color ??= dxf.Color; overlay.Fill ??= dxf.Fill;
        overlay.Left ??= dxf.Left; overlay.Right ??= dxf.Right; overlay.Top ??= dxf.Top; overlay.Bottom ??= dxf.Bottom;
    }

    private static CellStyle Merge(CellStyle cell, Overlay o) => new()
    {
        Bold = o.Bold ?? cell.Bold, Italic = o.Italic ?? cell.Italic, Underline = o.Underline ?? cell.Underline, Strike = o.Strike ?? cell.Strike,
        Color = o.Color ?? cell.Color, Fill = o.Fill ?? cell.Fill, Pattern = o.Fill is null ? cell.Pattern : null, Size = cell.Size, Font = cell.Font, Wrap = cell.Wrap, VAlign = cell.VAlign, Indent = cell.Indent,
        Left = o.Left ?? cell.Left, Right = o.Right ?? cell.Right, Top = o.Top ?? cell.Top, Bottom = o.Bottom ?? cell.Bottom,
        Bar = o.Bar ?? cell.Bar, Icon = o.Icon ?? cell.Icon
    };

    // A rule's formula when it is a constant: a number, "text" or TRUE/FALSE; null for anything to calculate.
    private static (double? Number, string? Text)? Constant(string formula)
    {
        string f = formula.Trim();
        if (double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n)) return (n, null);
        if (f.Length >= 2 && f[0] == '"' && f[^1] == '"' && !f[1..^1].Replace("\"\"", "").Contains('"')) return (null, f[1..^1].Replace("\"\"", "\""));
        if (f is "TRUE" or "FALSE") return (null, f);
        return null;
    }

    // A scale point: the lowest or highest value, a number, a percent of the range, or a percentile.
    private static double? Threshold(string type, string value, List<double> values)
    {
        if (values.Count == 0) return 0;
        double low = values.Min(), high = values.Max();
        double? parsed = Constant(value) is { Number: double n } ? n : null;
        return type switch
        {
            "min" or "autoMin" => low,
            "max" or "autoMax" => high,
            "num" or "formula" => parsed,
            "percent" => parsed is double p ? low + (high - low) * p / 100 : null,
            "percentile" => parsed is double q ? Percentile(values, q) : null,
            _ => null
        };
    }

    private static double Percentile(List<double> values, double percent)
    {
        var sorted = values.OrderBy(v => v).ToList();
        double rank = Math.Clamp(percent, 0, 100) / 100 * (sorted.Count - 1);
        int below = (int)Math.Floor(rank);
        return below + 1 < sorted.Count ? sorted[below] + (rank - below) * (sorted[below + 1] - sorted[below]) : sorted[below];
    }

    // The colour for value v between two or three scale points.
    private static string Scale(double v, List<double> limits, List<string?> colours)
    {
        if (v <= limits[0]) return colours[0]!;
        for (int i = 1; i < limits.Count; i++)
        {
            if (v > limits[i] && i < limits.Count - 1) continue;
            double t = limits[i] > limits[i - 1] ? Math.Clamp((v - limits[i - 1]) / (limits[i] - limits[i - 1]), 0, 1) : 1;
            return Mix(colours[i - 1]!, colours[i]!, t);
        }
        return colours[^1]!;
    }

    private static string Mix(string a, string b, double t)
    {
        int Channel(int at) => (int)Math.Round(Convert.ToInt32(a.Substring(at, 2), 16) * (1 - t) + Convert.ToInt32(b.Substring(at, 2), 16) * t);
        return $"#{Channel(1):x2}{Channel(3):x2}{Channel(5):x2}";
    }

    // Excel's icon sets, lowest values first, as the shapes and colours the grid page draws.
    private static string[]? Icons(string set) => set switch
    {
        "3Arrows" => ["arrow-down red", "arrow-right yellow", "arrow-up green"],
        "3ArrowsGray" => ["arrow-down gray", "arrow-right gray", "arrow-up gray"],
        "4Arrows" => ["arrow-down red", "arrow-down-right yellow", "arrow-up-right yellow", "arrow-up green"],
        "4ArrowsGray" => ["arrow-down gray", "arrow-down-right gray", "arrow-up-right gray", "arrow-up gray"],
        "5Arrows" => ["arrow-down red", "arrow-down-right yellow", "arrow-right yellow", "arrow-up-right yellow", "arrow-up green"],
        "5ArrowsGray" => ["arrow-down gray", "arrow-down-right gray", "arrow-right gray", "arrow-up-right gray", "arrow-up gray"],
        "3TrafficLights1" or "3TrafficLights2" or "3Signs" => ["circle red", "circle yellow", "circle green"],
        "4TrafficLights" => ["circle black", "circle red", "circle yellow", "circle green"],
        "4RedToBlack" => ["circle black", "circle gray", "circle pink", "circle red"],
        "3Symbols" or "3Symbols2" => ["cross red", "exclamation yellow", "check green"],
        "3Flags" => ["flag red", "flag yellow", "flag green"],
        "3Stars" => ["star-empty gray", "star-half yellow", "star-full yellow"],
        "4Rating" => ["bar-1 blue", "bar-2 blue", "bar-3 blue", "bar-4 blue"],
        "5Rating" or "5Boxes" => ["bar-0 blue", "bar-1 blue", "bar-2 blue", "bar-3 blue", "bar-4 blue"],
        "5Quarters" => ["quarter-0 gray", "quarter-1 gray", "quarter-2 gray", "quarter-3 gray", "quarter-4 gray"],
        "3Triangles" => ["triangle-down red", "dash yellow", "triangle-up green"],
        _ => null
    };
}
