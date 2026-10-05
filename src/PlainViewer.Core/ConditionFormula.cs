using System.Globalization;
namespace PlainViewer.Core;

// The formulas of conditional formatting rules ("use a formula to decide which cells to format", and value rules
// that compare with a formula), evaluated over the values saved in the sheet's cells. Only the rule's formula is
// worked out; cell formulas are never recalculated (their saved results are the values used). A formula is relative to
// the top-left cell of the rule's first range: references without $ move with each cell, as in Excel.
// Supported: numbers, text, TRUE/FALSE, references and ranges on the same sheet, + - * / ^ & = <> < > <= >= and
// unary minus and percent, and the functions listed in Call. Anything else (other sheets, names, other functions,
// today's date) makes Parse return null, and the rule stays counted as not shown. So do formulas longer than Excel's
// 8,192 characters, nested deeper than Excel's 64 levels or with more than 512 operations (their evaluation would
// recurse that deep). One instance serves one rule on one sheet: ranges with only fixed ($) references are read once.
internal sealed class ConditionFormula
{
    public sealed class Error { public static readonly Error Value = new(); }
    private abstract record Node;
    private sealed record Literal(object? Value) : Node;
    private sealed record Reference(int Row, int Column, bool RowFixed, bool ColumnFixed) : Node;
    private sealed record Range(Reference From, Reference To) : Node;
    private sealed record Unary(char Op, Node Operand) : Node;
    private sealed record Binary(string Op, Node Left, Node Right) : Node;
    private sealed record Function(string Name, List<Node> Arguments) : Node;

    private readonly Node root;
    private ConditionFormula(Node root) => this.root = root;

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "XOR", "IF", "IFERROR", "ISBLANK", "ISNUMBER", "ISTEXT", "ISNONTEXT", "ISERROR", "ISERR", "ISNA", "ISLOGICAL", "ISEVEN", "ISODD",
        "ROW", "COLUMN", "MOD", "ABS", "INT", "ROUND", "ROUNDUP", "ROUNDDOWN", "LEN", "LEFT", "RIGHT", "MID", "UPPER", "LOWER", "TRIM", "EXACT",
        "SEARCH", "FIND", "VALUE", "SUM", "AVERAGE", "MIN", "MAX", "COUNT", "COUNTA", "COUNTBLANK", "COUNTIF", "TRUE", "FALSE",
    };

    // ---- Parsing ----

    public static ConditionFormula? Parse(string formula)
    {
        if (formula.Length > 8192) return null;
        try
        {
            var parser = new Parser(formula.Trim().TrimStart('='));
            var node = parser.Expression();
            return parser.AtEnd ? new ConditionFormula(node) { CellsPerCell = parser.MovingCells } : null;
        }
        catch (FormatException) { return null; }
    }

    // Cells read for each cell the rule covers by ranges that move with the cell (fixed ranges are read only once):
    // callers compare it with the number of cells covered and leave rules too costly to work out as not shown.
    public long CellsPerCell { get; private init; }
    private readonly Dictionary<Range, List<object?>> fixedRanges = [];

    private sealed class Parser(string text)
    {
        private int at, depth, operations;
        public long MovingCells { get; private set; }
        private T Count<T>(T node) { if (++operations > 512) throw Unsupported(); return node; }
        public bool AtEnd { get { Skip(); return at >= text.Length; } }
        private void Skip() { while (at < text.Length && char.IsWhiteSpace(text[at])) at++; }
        private bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, at, token, 0, token.Length) != 0) return false;
            at += token.Length; return true;
        }
        private static FormatException Unsupported() => new();

        public Node Expression()
        {
            if (++depth > 64) throw Unsupported();
            var node = Comparison();
            depth--;
            return node;
        }
        private Node Comparison()
        {
            var left = Concatenation();
            while (true)
            {
                string? op = Take("<=") ? "<=" : Take(">=") ? ">=" : Take("<>") ? "<>" : Take("=") ? "=" : Take("<") ? "<" : Take(">") ? ">" : null;
                if (op is null) return left;
                left = Count(new Binary(op, left, Concatenation()));
            }
        }
        private Node Concatenation() { var left = Additive(); while (Take("&")) left = Count(new Binary("&", left, Additive())); return left; }
        private Node Additive()
        {
            var left = Multiplicative();
            while (true) { if (Take("+")) left = Count(new Binary("+", left, Multiplicative())); else if (Take("-")) left = Count(new Binary("-", left, Multiplicative())); else return left; }
        }
        private Node Multiplicative()
        {
            var left = Power();
            while (true) { if (Take("*")) left = Count(new Binary("*", left, Power())); else if (Take("/")) left = Count(new Binary("/", left, Power())); else return left; }
        }
        private Node Power() { var left = Prefix(); while (Take("^")) left = Count(new Binary("^", left, Prefix())); return left; }
        private Node Prefix()
        {
            // Signs in a row, counted without recursion: an odd number of minus signs negates.
            bool negative = false;
            while (true) { if (Take("-")) negative = !negative; else if (!Take("+")) break; if (++operations > 512) throw Unsupported(); }
            var node = Primary();
            while (Take("%")) node = Count(new Binary("/", node, new Literal(100.0)));
            return negative ? Count(new Unary('-', node)) : node;
        }
        private Node Primary()
        {
            Skip();
            if (at >= text.Length) throw Unsupported();
            char c = text[at];
            if (c == '(') { at++; var inner = Expression(); if (!Take(")")) throw Unsupported(); return inner; }
            if (c == '"')
            {
                var value = new System.Text.StringBuilder();
                for (at++; ; at++)
                {
                    if (at >= text.Length) throw Unsupported();
                    if (text[at] == '"') { if (at + 1 < text.Length && text[at + 1] == '"') { value.Append('"'); at++; continue; } at++; break; }
                    value.Append(text[at]);
                }
                return new Literal(value.ToString());
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = at;
                while (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.')) at++;
                if (at < text.Length && text[at] is 'e' or 'E') { at++; if (at < text.Length && text[at] is '+' or '-') at++; while (at < text.Length && char.IsDigit(text[at])) at++; }
                return new Literal(double.Parse(text[start..at], NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            if (char.IsLetter(c) || c == '$' || c == '_')
            {
                int start = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '$' or '_' or '.')) at++;
                string word = text[start..at];
                if (at < text.Length && text[at] == '!') throw Unsupported();                 // another sheet
                Skip();
                if (at < text.Length && text[at] == '(')
                {
                    if (!Functions.Contains(word)) throw Unsupported();
                    at++;
                    var arguments = new List<Node>();
                    if (!Take(")"))
                    {
                        do arguments.Add(Expression()); while (Take(","));
                        if (!Take(")")) throw Unsupported();
                    }
                    return Count(new Function(word.ToUpperInvariant(), arguments));
                }
                if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return new Literal(true);
                if (word.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return new Literal(false);
                var reference = Cell(word) ?? throw Unsupported();                            // defined names are not supported
                if (Take(":"))
                {
                    Skip();
                    int s2 = at;
                    while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '$')) at++;
                    var to = Cell(text[s2..at]) ?? throw Unsupported();
                    if (!(reference.RowFixed && reference.ColumnFixed && to.RowFixed && to.ColumnFixed))
                        MovingCells += (long)(Math.Abs(to.Row - reference.Row) + 1) * (Math.Abs(to.Column - reference.Column) + 1);
                    return new Range(reference, to);
                }
                return reference;
            }
            throw Unsupported();
        }

        // "B2", "$B$2", "B$2": zero-based row and column.
        private static Reference? Cell(string word)
        {
            int i = 0; bool columnFixed = false, rowFixed = false;
            if (i < word.Length && word[i] == '$') { columnFixed = true; i++; }
            int column = 0, letters = 0;
            while (i < word.Length && char.IsAsciiLetter(word[i]) && letters < 3) { column = column * 26 + char.ToUpperInvariant(word[i]) - 'A' + 1; i++; letters++; }
            if (letters == 0) return null;
            if (i < word.Length && word[i] == '$') { rowFixed = true; i++; }
            if (i >= word.Length || !int.TryParse(word.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1) return null;
            return new Reference(row - 1, column - 1, rowFixed, columnFixed);
        }
    }

    // ---- Evaluation ----

    // Whether the formula is true for the cell at (row, column), the rule's first cell being (originRow, originColumn).
    public bool IsTrue(int row, int column, int originRow, int originColumn, Func<int, int, object?> cell) =>
        Evaluate(row, column, originRow, originColumn, cell) switch { bool b => b, double d => d != 0, _ => false };

    public object? Evaluate(int row, int column, int originRow, int originColumn, Func<int, int, object?> cell)
    {
        var context = new Context(row - originRow, column - originColumn, cell, fixedRanges) { OriginRow = originRow, OriginColumn = originColumn };
        try { return Scalar(context.Value(root)); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidCastException or IndexOutOfRangeException) { return Error.Value; }
    }

    private static object? Scalar(object? value) => value is List<object?> list ? list.FirstOrDefault() : value;

    private sealed class Context(int rowShift, int columnShift, Func<int, int, object?> cell, Dictionary<Range, List<object?>> fixedRanges)
    {
        private (int Row, int Column) At(Reference r) => (r.RowFixed ? r.Row : r.Row + rowShift, r.ColumnFixed ? r.Column : r.Column + columnShift);

        public object? Value(Node node)
        {
            switch (node)
            {
                case Literal l: return l.Value;
                case Reference r: { var (row, column) = At(r); return row < 0 || column < 0 ? Error.Value : cell(row, column); }
                case Range g:
                    {
                        if (fixedRanges.TryGetValue(g, out var known)) return known;
                        var (r1, c1) = At(g.From); var (r2, c2) = At(g.To);
                        if (r1 < 0 || c1 < 0 || r2 < 0 || c2 < 0 || (long)(Math.Abs(r2 - r1) + 1) * (Math.Abs(c2 - c1) + 1) > 100_000) return Error.Value;
                        var values = new List<object?>();
                        for (int r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                            for (int c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++) values.Add(cell(r, c));
                        if (g.From.RowFixed && g.From.ColumnFixed && g.To.RowFixed && g.To.ColumnFixed) fixedRanges[g] = values;
                        return values;
                    }
                case Unary u: { var v = Number(Scalar(Value(u.Operand))); return v is double d ? -d : Error.Value; }
                case Binary b: return BinaryValue(b);
                case Function f: return Call(f);
                default: return Error.Value;
            }
        }

        private object? BinaryValue(Binary b)
        {
            object? left = Scalar(Value(b.Left)), right = Scalar(Value(b.Right));
            if (left is Error) return left;
            if (right is Error) return right;
            if (b.Op == "&") return Text(left) + Text(right);
            if (b.Op is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                int order = Compare(left, right);
                return b.Op switch { "=" => order == 0, "<>" => order != 0, "<" => order < 0, ">" => order > 0, "<=" => order <= 0, _ => order >= 0 };
            }
            if (Number(left) is not double x || Number(right) is not double y) return Error.Value;
            double result = b.Op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => y == 0 ? double.NaN : x / y, _ => Math.Pow(x, y) };
            return double.IsFinite(result) ? result : Error.Value;
        }

        private object? Call(Function f)
        {
            var a = f.Arguments;
            object? Arg(int i) => i < a.Count ? Scalar(Value(a[i])) : null;
            double? Num(int i) => i < a.Count ? Number(Arg(i)) : null;      // null: an optional argument not given
            IEnumerable<object?> All() => a.SelectMany(n => Value(n) is List<object?> list ? list : [Scalar(Value(n))]);
            switch (f.Name)
            {
                case "TRUE": return true;
                case "FALSE": return false;
                case "AND": { var values = All().Where(v => v is not null and not string).ToList(); return values.Any(v => v is Error) ? Error.Value : values.All(Truthy); }
                case "OR": { var values = All().Where(v => v is not null and not string).ToList(); return values.Any(v => v is Error) ? Error.Value : values.Any(Truthy); }
                case "XOR": return All().Where(v => v is not null and not string).Count(Truthy) % 2 == 1;
                case "NOT": { var v = Arg(0); return v is Error ? v : !Truthy(v); }
                case "IF": { var test = Arg(0); return test is Error ? test : Truthy(test) ? (a.Count > 1 ? Arg(1) : true) : (a.Count > 2 ? Arg(2) : false); }
                case "IFERROR": { var v = Arg(0); return v is Error ? Arg(1) : v; }
                case "ISBLANK": return Arg(0) is null;
                case "ISNUMBER": return Arg(0) is double;
                case "ISTEXT": return Arg(0) is string;
                case "ISNONTEXT": return Arg(0) is not string;
                case "ISLOGICAL": return Arg(0) is bool;
                case "ISERROR" or "ISERR" or "ISNA": return Arg(0) is Error;
                case "ISEVEN": return Num(0) is double e ? Math.Floor(Math.Abs(e)) % 2 == 0 : Error.Value;
                case "ISODD": return Num(0) is double o ? Math.Floor(Math.Abs(o)) % 2 == 1 : Error.Value;
                case "ROW": return a.Count == 0 ? (double)(rowShift + OriginRow + 1) : a[0] is Reference r ? (double)(At(r).Row + 1) : a[0] is Range g ? (double)(At(g.From).Row + 1) : Error.Value;
                case "COLUMN": return a.Count == 0 ? (double)(columnShift + OriginColumn + 1) : a[0] is Reference c ? (double)(At(c).Column + 1) : a[0] is Range h ? (double)(At(h.From).Column + 1) : Error.Value;
                case "MOD": { if (Num(0) is not double n || Num(1) is not double d || d == 0) return Error.Value; return n - d * Math.Floor(n / d); }
                case "ABS": return Num(0) is double abs ? Math.Abs(abs) : Error.Value;
                case "INT": return Num(0) is double whole ? Math.Floor(whole) : Error.Value;
                case "ROUND" or "ROUNDUP" or "ROUNDDOWN":
                    {
                        if (Num(0) is not double v || Num(1) is double.NaN) return Error.Value;
                        double digits = Math.Truncate(Num(1) ?? 0);
                        if (digits > 15) return v;                                         // already exact to that many places
                        // Far fewer digits than the number has: ROUND and ROUNDDOWN give 0, ROUNDUP a number too big to hold.
                        if (digits < -300) return v == 0 || f.Name != "ROUNDUP" ? 0.0 : Error.Value;
                        double scale = Math.Pow(10, digits), s = v * scale;
                        double result = (f.Name == "ROUND" ? Math.Round(s, MidpointRounding.AwayFromZero) : f.Name == "ROUNDUP" ? Math.Sign(s) * Math.Ceiling(Math.Abs(s)) : Math.Truncate(s)) / scale;
                        return double.IsFinite(result) ? result : Error.Value;
                    }
                case "LEN": return (double)Text(Arg(0)).Length;
                case "LEFT": { string t = Text(Arg(0)); int n = (int)(Num(1) ?? 1); return n < 0 ? Error.Value : t[..Math.Min(n, t.Length)]; }
                case "RIGHT": { string t = Text(Arg(0)); int n = (int)(Num(1) ?? 1); return n < 0 ? Error.Value : t[Math.Max(0, t.Length - n)..]; }
                case "MID": { string t = Text(Arg(0)); int s = (int)(Num(1) ?? 0), n = (int)(Num(2) ?? 0); return s < 1 || n < 0 ? Error.Value : s > t.Length ? "" : t.Substring(s - 1, Math.Min(n, t.Length - s + 1)); }
                case "UPPER": return Text(Arg(0)).ToUpperInvariant();
                case "LOWER": return Text(Arg(0)).ToLowerInvariant();
                case "TRIM": return string.Join(' ', Text(Arg(0)).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                case "EXACT": return Text(Arg(0)) == Text(Arg(1));
                case "SEARCH" or "FIND":
                    {
                        string needle = Text(Arg(0)), haystack = Text(Arg(1));
                        int from = (int)(Num(2) ?? 1) - 1;
                        if (from < 0 || from > haystack.Length) return Error.Value;
                        int found = haystack.IndexOf(needle, from, f.Name == "SEARCH" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                        return found < 0 ? Error.Value : (double)(found + 1);
                    }
                case "VALUE": return Number(Arg(0)) is double value ? value : Error.Value;
                case "SUM": return All().OfType<double>().Sum();
                case "AVERAGE": { var n = All().OfType<double>().ToList(); return n.Count == 0 ? Error.Value : n.Average(); }
                case "MIN": { var n = All().OfType<double>().ToList(); return n.Count == 0 ? 0.0 : n.Min(); }
                case "MAX": { var n = All().OfType<double>().ToList(); return n.Count == 0 ? 0.0 : n.Max(); }
                case "COUNT": return (double)All().OfType<double>().Count();
                case "COUNTA": return (double)All().Count(v => v is not null);
                case "COUNTBLANK": return (double)All().Count(v => v is null || v is string { Length: 0 });
                case "COUNTIF":
                    {
                        if (a.Count < 2 || Value(a[0]) is not List<object?> range) return Error.Value;
                        var criterion = Arg(1);
                        string op = "="; object? target = criterion;
                        if (criterion is string text)
                        {
                            foreach (var prefix in new[] { "<=", ">=", "<>", "=", "<", ">" })
                                if (text.StartsWith(prefix, StringComparison.Ordinal)) { op = prefix; text = text[prefix.Length..]; break; }
                            target = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : text;
                            // Wildcards in text: * any run, ? one character, ~ makes the next one literal; text cells only.
                            if (target is string pattern && op is "=" or "<>" && pattern.IndexOfAny(['*', '?']) >= 0)
                            {
                                var regex = new System.Text.RegularExpressions.Regex("^" + Wildcard(pattern) + "$",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                                return (double)range.Count(v => (v is string s && regex.IsMatch(s)) == (op == "="));
                            }
                        }
                        return (double)range.Count(v =>
                        {
                            int order = Compare(v ?? (target is double ? null : ""), target);
                            if (target is double && v is not double) return op == "<>";
                            return op switch { "=" => order == 0, "<>" => order != 0, "<" => order < 0, ">" => order > 0, "<=" => order <= 0, _ => order >= 0 };
                        });
                    }
                default: return Error.Value;
            }
        }

        public int OriginRow { get; init; }
        public int OriginColumn { get; init; }
    }

    // Excel's ordering: numbers before text before TRUE/FALSE; text without case; an empty cell is 0, "" or FALSE.
    private static int Compare(object? left, object? right)
    {
        left ??= right switch { string => "", bool => false, _ => 0.0 };
        right ??= left switch { string => "", bool => false, _ => 0.0 };
        static int Rank(object v) => v switch { double => 0, string => 1, _ => 2 };
        int rank = Rank(left!).CompareTo(Rank(right!));
        if (rank != 0) return rank;
        return left switch
        {
            double x => x.CompareTo((double)right!),
            string s => Math.Sign(string.Compare(s, (string)right!, StringComparison.OrdinalIgnoreCase)),
            bool p => p.CompareTo((bool)right!),
            _ => 0
        };
    }

    private static string Wildcard(string pattern)
    {
        var regex = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '~' && i + 1 < pattern.Length) regex.Append(System.Text.RegularExpressions.Regex.Escape(pattern[++i].ToString()));
            else regex.Append(c switch { '*' => ".*", '?' => ".", _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()) });
        }
        return regex.ToString();
    }

    private static bool Truthy(object? v) => v switch { bool b => b, double d => d != 0, _ => false };
    private static double? Number(object? v) => v switch
    {
        double d => d, bool b => b ? 1 : 0, null => 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null,
        _ => null
    };
    private static string Text(object? v) => v switch
    {
        null => "", string s => s, bool b => b ? "TRUE" : "FALSE", double d => d.ToString("G15", CultureInfo.InvariantCulture), _ => "#VALUE!"
    };
}
