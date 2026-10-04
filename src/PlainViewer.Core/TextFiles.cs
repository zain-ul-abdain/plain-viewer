using System.Text;
namespace PlainViewer.Core;

public static class TextFiles
{
    public const long TextLimit = 4 * 1024 * 1024;
    // Plain-text formats are shown as text and never parsed: XML entities, JSON and YAML stay exactly as written, and
    // source code and project files (0.8.0) are only read, never run or built.
    public static readonly string[] DataExtensions = [".txt", ".json", ".xml", ".log", ".ini", ".yaml", ".yml"];
    public static readonly string[] CodeExtensions = [".c", ".h", ".cs", ".java", ".js", ".vb", ".css", ".php", ".asp", ".aspx", ".razor", ".config", ".csproj", ".sln"];
    public static readonly string[] PlainExtensions = [.. DataExtensions, .. CodeExtensions];
    // Delimited data shown in the grid; .tsv starts with the tab as its delimiter.
    public static readonly string[] DelimitedExtensions = [".csv", ".tsv"];
    public static readonly string[] Extensions = [.. PlainExtensions, .. DelimitedExtensions, ".md", ".markdown"];
    public static void ValidateLocalPath(string path)
    {
        // Reject network/device paths before filesystem access can contact them.
        if (path.StartsWith(@"\\") || path.StartsWith("//") || path.Contains("://") ||
            !Path.IsPathFullyQualified(path) || path[1] != ':' || path[2..].Contains(':'))
            throw new DocumentException("Choose a file on a local drive. Network and device paths cannot be opened.");
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new DocumentException("Copy this file to a local drive before opening it.");
        string? current = path;
        while (!string.IsNullOrEmpty(current) && current.Length > root.Length)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 || ((int)attributes & (0x40000 | 0x400000)) != 0)
                throw new DocumentException("This file or folder is a link or is not available offline. Copy it to a regular local folder first.");
            current = Path.GetDirectoryName(current);
        }
    }
    public static (Encoding Encoding, int Skip) Detect(byte[] sample, string choice = "Auto")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (choice != "Auto") return (choice switch {
            "UTF-8" => new UTF8Encoding(false, true), "UTF-16 LE" => new UnicodeEncoding(false, false, true),
            "UTF-16 BE" => new UnicodeEncoding(true, false, true), "Windows-1252" => Encoding.GetEncoding(1252),
            _ => throw new DocumentException("Choose a supported text encoding.") }, 0);
        if (sample.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(false, true), 3);
        if (sample.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, false, true), 2);
        if (sample.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, false, true), 2);
        if (sample.Length >= 4)
        {
            int even = 0, odd = 0;
            for (int i = 0; i < sample.Length; i++) if (sample[i] == 0) { if (i % 2 == 0) even++; else odd++; }
            if (odd > sample.Length / 5 && even == 0) return (new UnicodeEncoding(false, false, true), 0);
            if (even > sample.Length / 5 && odd == 0) return (new UnicodeEncoding(true, false, true), 0);
        }
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            utf8.GetDecoder().Convert(sample, new char[sample.Length], false, out _, out _, out _);
            return (utf8, 0);
        }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(1252), 0); }
    }
    // With a row store (the app always passes a work folder), CSV files and large text files stream to disk.
    public const long StoreLimit = 1024L * 1024 * 1024;
    private static readonly System.Buffers.SearchValues<char> Binary = System.Buffers.SearchValues.Create(
        Enumerable.Range(0, 0xA0).Select(i => (char)i).Where(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t' or '\f')).ToArray());

    public static DocumentView Load(string path, string encodingChoice = "Auto", string delimiterChoice = "Auto", string? storeFolder = null)
    {
        ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new DocumentException($"{extension} files do not open in the text view.");
        using var stream = LocalFiles.OpenRead(path);
        long originalLength = stream.Length;
        var stamp = LocalFiles.Stamp(stream);
        bool lines = storeFolder is not null && PlainExtensions.Contains(extension) && originalLength > TextLimit;
        bool delimited = DelimitedExtensions.Contains(extension);
        long limit = storeFolder is not null && (delimited || lines) ? StoreLimit : delimited ? 256L * 1024 * 1024 : TextLimit;
        if (originalLength > limit)
            throw new DocumentException(limit == StoreLimit ? "This file is larger than the 1 GB this viewer can open."
                : "This file exceeds the preview limit (4 MB for Markdown). Open it as plain text or in an editor.");
        byte[] sample = new byte[Math.Min(8192L, originalLength)]; stream.ReadExactly(sample);
        if (sample.AsSpan().StartsWith("%PDF-"u8) || sample.AsSpan().StartsWith("PK\u0003\u0004"u8) ||
            sample.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 }) || sample.AsSpan().StartsWith("MZ"u8))
            throw new DocumentException("The file contents do not match a text document. Open it with an application for its actual format.");
        var (encoding, skip) = Detect(sample, encodingChoice); stream.Position = skip;
        using var reader = new StreamReader(stream, encoding, encodingChoice == "Auto", 8192, true);
        var view = new DocumentView { Encoding = encoding.WebName };
        if (delimited)
        {
            // Detection tolerates a sample ending inside a UTF-8 character.
            string prefix = Encoding.GetEncoding(encoding.CodePage).GetString(sample.AsSpan(skip));
            char delimiter = delimiterChoice switch { "Comma" => ',', "Semicolon" => ';', "Tab" => '\t', _ => extension == ".tsv" ? '\t' : Csv.DetectDelimiter(prefix) };
            view.Kind = "csv"; view.Delimiter = delimiter;
            if (storeFolder is not null)
            {
                using var store = new RowStoreWriter(storeFolder, "rows");
                foreach (var row in Csv.Read(reader, delimiter))
                {
                    foreach (var field in row) if (field.Contains('\0')) throw new DocumentException("This CSV contains binary data. Choose another encoding or a valid CSV file.");
                    store.Add(row);
                }
                store.Complete(); view.Store = "rows"; view.RowCount = store.Count; view.Columns = store.Columns;
            }
            else
            {
                view.Rows = Csv.Read(reader, delimiter).Take(1001).ToList();
                if (view.Rows.SelectMany(row => row).Any(field => field.Contains('\0')))
                    throw new DocumentException("This CSV contains binary data. Choose another encoding or a valid CSV file.");
                if (view.Rows.Count > 1000) { view.Rows.RemoveAt(1000); view.Notice = "Preview: first 1,000 rows only. Remaining rows are not loaded."; }
            }
        }
        else if (lines)
        {
            // Large plain text: one row per line, shown in a scrolling list instead of one text box.
            using var store = new RowStoreWriter(storeFolder!, "rows");
            var row = new string[1];
            for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                if (store.Count == 0) line = line.TrimStart('﻿');
                if (line.AsSpan().ContainsAny(Binary)) throw new DocumentException("This file contains binary data, so its contents are not text. Open it with an application for its actual format.");
                row[0] = line; store.Add(row);
            }
            store.Complete(); view.Kind = "lines"; view.Store = "rows"; view.RowCount = store.Count; view.Columns = 1;
        }
        else
        {
            view.Text = reader.ReadToEnd().TrimStart('\ufeff');
            if (view.Text.Any(c => c == '\0' || (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t' && c != '\f')))
                throw new DocumentException("This file contains binary data, so its contents are not text. Open it with an application for its actual format.");
            view.Kind = extension is ".md" or ".markdown" ? "markdown" : "text";
            if (view.Kind == "text") view.Spans = CodeHighlighter.Spans(view.Text, extension);
            if (view.Kind == "markdown") view.Blocks = MarkdownView.Parse(view.Text);
        }
        LocalFiles.ThrowIfChanged(stream, stamp);
        return view;
    }
}
