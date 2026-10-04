namespace PlainViewer.Core;

// Syntax colouring for source code, project and data files shown as text. Runs in the worker over the text only; it
// never changes the text, so search and copy see exactly what the file holds. The result is a flat list of
// (start, length, kind) triples in DocumentView.Spans; kinds are below. Simple, forgiving rules per family of
// languages, not a parser: a misjudged span only changes a colour.
public static class CodeHighlighter
{
    public const int Comment = 1, String = 2, Keyword = 3, Number = 4, Markup = 5, Name = 6;
    public const int MaxLength = 256 * 1024;   // larger files are shown without colours

    private static readonly HashSet<string> CLike = new(StringComparer.Ordinal)
    {
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for", "goto", "if",
        "inline", "int", "long", "register", "return", "short", "signed", "sizeof", "static", "struct", "switch", "typedef", "union", "unsigned",
        "void", "volatile", "while", "bool", "true", "false", "NULL", "nullptr", "include", "define", "ifdef", "ifndef", "endif", "pragma",
    };
    private static readonly HashSet<string> CSharp = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float",
        "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref", "required",
        "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "yield",
    };
    private static readonly HashSet<string> Java = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double", "else",
        "enum", "extends", "final", "finally", "float", "for", "if", "implements", "import", "instanceof", "int", "interface", "long", "native",
        "new", "null", "package", "private", "protected", "public", "return", "short", "static", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "true", "false", "try", "var", "void", "volatile", "while", "record", "sealed", "yield",
    };
    private static readonly HashSet<string> JavaScript = new(StringComparer.Ordinal)
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "export",
        "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static",
        "super", "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "with", "yield",
    };
    private static readonly HashSet<string> Php = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "array", "as", "break", "case", "catch", "class", "const", "continue", "declare", "default", "do", "echo", "else",
        "elseif", "empty", "endif", "endforeach", "endwhile", "extends", "false", "final", "finally", "fn", "for", "foreach", "function",
        "global", "if", "implements", "include", "include_once", "instanceof", "interface", "isset", "match", "namespace", "new", "null", "or",
        "print", "private", "protected", "public", "readonly", "require", "require_once", "return", "static", "switch", "throw", "trait",
        "true", "try", "unset", "use", "var", "while", "yield",
    };
    private static readonly HashSet<string> VisualBasic = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddHandler", "And", "AndAlso", "As", "Boolean", "ByRef", "Byte", "ByVal", "Call", "Case", "Catch", "Class", "Const", "Dim", "Do",
        "Double", "Each", "Else", "ElseIf", "End", "Enum", "Exit", "False", "Finally", "For", "Friend", "Function", "Get", "Handles", "If",
        "Implements", "Imports", "In", "Inherits", "Integer", "Interface", "Is", "Long", "Loop", "Me", "Module", "MyBase", "Namespace", "New",
        "Next", "Not", "Nothing", "Object", "Of", "Or", "OrElse", "Overrides", "Private", "Property", "Protected", "Public", "ReadOnly",
        "Return", "Select", "Set", "Shared", "Single", "Static", "String", "Structure", "Sub", "Then", "Throw", "To", "True", "Try", "Using",
        "Wend", "While", "With", "Option", "Explicit", "Response", "Request", "Server", "Session",
    };

    private enum Family { CLike, Css, VisualBasic, Markup, Json, Yaml, Ini }

    // The spans for a file with this extension, or none for text this view does not colour.
    public static List<int> Spans(string text, string extension)
    {
        var spans = new List<int>();
        if (text.Length > MaxLength) return spans;
        (Family family, HashSet<string>? keywords) = extension switch
        {
            ".c" or ".h" => (Family.CLike, CLike),
            ".cs" => (Family.CLike, CSharp),
            ".java" => (Family.CLike, Java),
            ".js" => (Family.CLike, JavaScript),
            ".php" => (Family.CLike, Php),
            ".css" => (Family.Css, null),
            ".vb" => (Family.VisualBasic, VisualBasic),
            ".asp" or ".aspx" or ".razor" or ".config" or ".csproj" or ".xml" => (Family.Markup, null),
            ".json" => (Family.Json, null),
            ".yaml" or ".yml" => (Family.Yaml, null),
            ".ini" => (Family.Ini, null),
            _ => ((Family)(-1), null),
        };
        if ((int)family < 0) return spans;
        void Add(int start, int length, int kind) { if (length > 0) { spans.Add(start); spans.Add(length); spans.Add(kind); } }
        int n = text.Length;

        if (family == Family.Markup)
        {
            for (int i = 0; i < n;)
            {
                if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0) { int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal); end = end < 0 ? n : end + 3; Add(i, end - i, Comment); i = end; }
                else if (string.CompareOrdinal(text, i, "<%", 0, 2) == 0) { int end = text.IndexOf("%>", i + 2, StringComparison.Ordinal); end = end < 0 ? n : end + 2; Add(i, end - i, Keyword); i = end; }
                else if (text[i] == '<' && i + 1 < n && (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '?' or '!'))
                {
                    int start = i++; while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '/' or '?' or '!' or ':' or '-' or '.' or '_')) i++;
                    Add(start, i - start, Markup);
                    while (i < n && text[i] != '>')
                    {
                        if (text[i] is '"' or '\'') { int end = text.IndexOf(text[i], i + 1); end = end < 0 ? n : end + 1; Add(i, end - i, String); i = end; }
                        else if (char.IsLetter(text[i])) { int a = i; while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is ':' or '-' or '_' or '.' or '@')) i++; Add(a, i - a, Name); }
                        else i++;
                    }
                    if (i < n) { Add(i, 1, Markup); i++; }
                }
                else if (text[i] == '@' && extension == ".razor" && i + 1 < n && char.IsLetter(text[i + 1])) { int a = i++; while (i < n && char.IsLetter(text[i])) i++; Add(a, i - a, Keyword); }
                else i++;
            }
            return spans;
        }

        for (int i = 0; i < n;)
        {
            char c = text[i];
            bool lineStart = i == 0 || text[i - 1] == '\n';
            // Comments.
            if (family is Family.CLike or Family.Css && c == '/' && i + 1 < n && text[i + 1] == '*')
            { int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); end = end < 0 ? n : end + 2; Add(i, end - i, Comment); i = end; continue; }
            if ((family == Family.CLike && c == '/' && i + 1 < n && text[i + 1] == '/') || (family == Family.CLike && extension == ".php" && c == '#') ||
                (family == Family.VisualBasic && c == '\'') || (family == Family.Yaml && c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) ||
                (family == Family.Ini && lineStart && c is ';' or '#'))
            { int end = text.IndexOf('\n', i); end = end < 0 ? n : end; Add(i, end - i, Comment); i = end; continue; }
            // Strings (with backslash escapes, except Visual Basic, which doubles its quotes).
            if (c is '"' || (c is '\'' && family is not (Family.VisualBasic or Family.Ini or Family.Yaml)) || (c == '`' && extension == ".js"))
            {
                int start = i++;
                bool verbatim = start > 0 && text[start - 1] == '@' && extension == ".cs";
                while (i < n && text[i] != c && (text[i] != '\n' || c == '`' || verbatim))
                {
                    if (text[i] == '\\' && family != Family.VisualBasic && !verbatim) i++;
                    i++;
                }
                if (i < n && text[i] == c) i++;
                int from = verbatim ? start - 1 : start;
                // JSON: a string followed by a colon is a key.
                int after = i; while (after < n && text[after] is ' ' or '\t') after++;
                Add(from, i - from, family == Family.Json && after < n && text[after] == ':' ? Name : String);
                continue;
            }
            // Numbers.
            if (char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                int start = i; while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                if (family != Family.Ini) Add(start, i - start, Number);
                continue;
            }
            // Words: keywords, VB comments written as REM, YAML and INI keys, CSS properties, JSON literals.
            if (char.IsLetter(c) || c is '_' or '$' or '#' or '@' or '.' or '-')
            {
                int start = i; i++;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '$')) i++;
                string word = text[start..i];
                if (family == Family.VisualBasic && word.Equals("REM", StringComparison.OrdinalIgnoreCase) && lineStart)
                { int end = text.IndexOf('\n', start); end = end < 0 ? n : end; Add(start, end - start, Comment); i = end; continue; }
                int next = i; while (next < n && text[next] is ' ' or '\t') next++;
                bool colon = next < n && text[next] == ':', equals = next < n && text[next] == '=';
                if (keywords?.Contains(word.TrimStart('#', '@', '$')) == true) Add(start, i - start, Keyword);
                else if (family == Family.Json && word is "true" or "false" or "null") Add(start, i - start, Keyword);
                else if (family is Family.Yaml && colon && LineStartsWith(text, start)) Add(start, i - start, Name);
                else if (family is Family.Ini && equals && LineStartsWith(text, start)) Add(start, i - start, Name);
                else if (family is Family.Css && colon) Add(start, i - start, Name);
                else if (family is Family.Css && (c is '.' or '#' || char.IsLetter(c)) && next < n && text[next] is '{' or ',') Add(start, i - start, Keyword);
                continue;
            }
            if (family == Family.Ini && lineStart && c == '[') { int end = text.IndexOf('\n', i); end = end < 0 ? n : end; Add(i, end - i, Keyword); i = end; continue; }
            i++;
        }
        return spans;
    }

    // Whether only spaces (and YAML list dashes) come before `at` on its line.
    private static bool LineStartsWith(string text, int at)
    {
        for (int i = at - 1; i >= 0 && text[i] != '\n'; i--) if (text[i] is not (' ' or '\t' or '-')) return false;
        return true;
    }
}
