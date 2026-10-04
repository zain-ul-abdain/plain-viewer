using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using PlainViewer.Core;

// Child mode for the low-integrity test: drop to low integrity, then report which folders Windows lets it write to.
if (args is ["--low-integrity-probe", var mediumFolder, var lowFolder])
{
    LowIntegrity.LowerCurrentProcess();
    Console.WriteLine($"{CanWrite(mediumFolder)} {CanWrite(lowFolder)}");
    return 0;
}

int passed = 0, failed = 0;
void Test(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; } }
void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
T Throws<T>(Action action) where T : Exception { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
string root = Path.Combine(Path.GetTempPath(), "PlainViewerTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    Test("CSV multiline, escaped quotes and leading zeros", () => {
        var rows = Csv.Read(new StringReader("id,name,note\r\n001,\"Ali, A\",\"line1\nline2 \"\"quote\"\"\"\r\n"), ',').ToList();
        Check(rows.Count == 2 && rows[1][0] == "001" && rows[1][1] == "Ali, A" && rows[1][2] == "line1\nline2 \"quote\""); });
    Test("CSV malformed quotes rejected", () => Throws<DocumentException>(() => Csv.Read(new StringReader("a,\"unfinished"), ',').ToList()));
    Test("CSV empty and trailing fields", () => { Check(!Csv.Read(new StringReader(""), ',').Any()); Check(Csv.Read(new StringReader("a,"), ',').Single().SequenceEqual(new[] { "a", "" })); });
    Test("CSV delimiter ignores quoted separators", () => Check(Csv.DetectDelimiter("\"a,b\";c\n") == ';'));
    Test("Full disk recognised, other IO errors not", () => {
        Check(DiskSpace.IsFull(new IOException("full", unchecked((int)0x80070070))) && DiskSpace.IsFull(new IOException("full", unchecked((int)0x80070027))));
        Check(!DiskSpace.IsFull(new IOException("locked", unchecked((int)0x80070020))) && !DiskSpace.IsFull(new InvalidDataException("x"))); });
    Test("CSV column resource limit", () => { Csv.Read(new StringReader(new string(',', 1000)), ',').ToList(); Throws<DocumentException>(() => Csv.Read(new StringReader(new string(',', Csv.MaxColumns)), ',').ToList()); });
    Test("UTF BOM and Windows-1252", () => { Check(TextFiles.Detect([255, 254, 65, 0]).Encoding.CodePage == 1200); Check(TextFiles.Detect([0x93, 65, 0x94]).Encoding.CodePage == 1252); });
    Test("UTF-16 heuristic", () => Check(TextFiles.Detect([65, 0, 66, 0, 67, 0]).Encoding.CodePage == 1200));
    Test("UTF-8 partial sample", () => Check(TextFiles.Detect([65, 0xe2, 0x82]).Encoding.CodePage == 65001));
    Test("Unsafe link schemes remain inert", () => { foreach (var link in new[] { "javascript:alert(1)", "file:///c:/secret", @"\\server\share", "data:text/html,test", "relative.md" }) Check(!LinkPolicy.CanOpen(link)); Check(LinkPolicy.CanOpen("https://example.com")); });
    Test("Opened links are encoded so they cannot add program arguments", () =>
    {
        foreach (var link in new[] { "mailto:boss@corp.example?subject=x\" /a \"C:\\Users\\v\\salary.xlsx", "https://example.com/a b\"c<d>e^f`g", "http://example.com/?q=\" --flag" })
        {
            string? launch = LinkPolicy.LaunchAddress(link);
            if (launch is not null && launch.Any(c => char.IsWhiteSpace(c) || c is '"' or '<' or '>' or '^' or '`')) throw new Exception($"{link} -> {launch}");
        }
        Check(LinkPolicy.LaunchAddress("https://example.com/a b\"c") == "https://example.com/a%20b%22c");
        Check(LinkPolicy.LaunchAddress("mailto:someone@example.com") == "mailto:someone@example.com");
        Check(LinkPolicy.LaunchAddress("javascript:alert(1)") is null && LinkPolicy.LaunchAddress(null) is null);
    });
    Test("Markdown literal HTML, no image references in display data", () => {
        var blocks = MarkdownView.Parse("# Hello\n\n<script>alert(1)</script>\n\n![private](file:///c:/secret.png)\n\n[bad](javascript:alert)\n");
        string json = System.Text.Json.JsonSerializer.Serialize(blocks); Check(blocks[0].Kind == "heading"); Check(json.Contains("script")); Check(!json.Contains("secret.png")); Check(!json.Contains("\"Link\":\"javascript")); });
    Test("Markdown tables and tasks", () => { var blocks = MarkdownView.Parse("| A | B |\n|---|---|\n| 1 | 2 |\n\n- [x] Done\n"); Check(blocks.Any(b => b.Kind == "table")); Check(blocks.Any(b => b.Kind == "list")); });
    Test("Front matter retained as code", () => Check(MarkdownView.Parse("---\nname: demo\n---\n# Hello")[0].Kind == "code"));
    Test("Mermaid stays code", () => Check(MarkdownView.Parse("```mermaid\ngraph TD; A-->B\n```")[0].Kind == "code"));
    Test("DTD and XXE prohibited", () => { using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///C:/secret'>]><a>&x;</a>")); using var reader = ArchiveSafety.CreateXmlReader(stream); Throws<XmlException>(() => { while (reader.Read()) { } }); });
    Test("Archive expansion limit", () => {
        using var stream = new MemoryStream(); using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, true)) { using var entry = writer.CreateEntry("large").Open(); entry.Write(new byte[1024 * 1024]); }
        stream.Position = 0; using var archive = new ZipArchive(stream, ZipArchiveMode.Read); Throws<DocumentException>(() => ArchiveSafety.Validate(archive, maximumBytes: 1000)); });
    Test("Archive traversal rejected", () => { using var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) zip.CreateEntry("../escape"); stream.Position = 0; using var archive = new ZipArchive(stream); Throws<DocumentException>(() => ArchiveSafety.Validate(archive)); });
    Test("Network path rejected before access", () => Throws<DocumentException>(() => TextFiles.ValidateLocalPath(@"\\nonexistent.invalid\share\secret.md")));
    Test("Original unchanged and no adjacent files", () => {
        var path = Path.Combine(root, "sample.txt"); File.WriteAllText(path, "مرحبا • سنڌي • اردو • 中文", new UTF8Encoding(false));
        byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var names = Directory.GetFiles(root);
        var loaded = TextFiles.Load(path); Check(loaded.Text.Contains("中文")); Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))); Check(names.SequenceEqual(Directory.GetFiles(root))); });
    Test("Source can remain open for writes", () => { string path = Path.Combine(root, "shared.txt"); File.WriteAllText(path, "readable"); using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); Check(TextFiles.Load(path).Text == "readable"); });
    // The opened file itself is checked (LocalFiles), not only its path, and change checks follow the opened file.
    Test("Opening checks the opened file: an offline file is refused even without the path check", () => {
        string path = Path.Combine(root, "offline.txt"); File.WriteAllText(path, "not here");
        File.SetAttributes(path, FileAttributes.Offline);
        try { Throws<DocumentException>(() => LocalFiles.OpenChecked(path).Dispose()); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    });
    Test("A link as the file itself is opened as a link and refused (skipped when this PC cannot make links)", () => {
        string target = Path.Combine(root, "link-target.txt"), link = Path.Combine(root, "link.txt"); File.WriteAllText(target, "target");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("  (symbolic links need Developer Mode or administrator rights here: skipped)"); return; }
        Throws<DocumentException>(() => LocalFiles.OpenChecked(link).Dispose());
        Throws<DocumentException>(() => TextFiles.Load(link));
    });
    Test("Opening reports a missing file, a missing folder, a folder and a locked file as such", () => {
        Check(Throws<DocumentException>(() => LocalFiles.OpenRead(Path.Combine(root, "no-such-file.txt")).Dispose()).Message == LocalFiles.MovedMessage);
        Check(Throws<DocumentException>(() => LocalFiles.OpenRead(Path.Combine(root, "no-such-folder", "file.txt")).Dispose()).Message == LocalFiles.MovedMessage);
        string folder = Path.Combine(root, "a-folder.txt"); Directory.CreateDirectory(folder);
        Check(Throws<Exception>(() => LocalFiles.OpenChecked(folder).Dispose()) is UnauthorizedAccessException or DocumentException);
        Check(Throws<DocumentException>(() => LocalFiles.OpenRead(folder).Dispose()).Message == LocalFiles.DeniedMessage);
        string locked = Path.Combine(root, "locked.txt"); File.WriteAllText(locked, "busy");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Throws<DocumentException>(() => LocalFiles.OpenRead(locked).Dispose()).Message == LocalFiles.LockedMessage);
        Check(LocalFiles.OpenRead(locked).Length == 4);                         // readable again once released
    });
    Test("A change made through another handle while open is detected", () => {
        string path = Path.Combine(root, "changing.txt"); File.WriteAllText(path, "first");
        using var stream = LocalFiles.OpenRead(path);
        var stamp = LocalFiles.Stamp(stream);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { writer.Seek(0, SeekOrigin.End); writer.Write("more"u8); }
        Throws<DocumentException>(() => LocalFiles.ThrowIfChanged(stream, stamp));
    });
    Test("Replacing the file at the same path after it was opened does not affect the opened file", () => {
        string path = Path.Combine(root, "replaced.txt"), moved = Path.Combine(root, "replaced-old.txt"); File.WriteAllText(path, "original");
        using var stream = LocalFiles.OpenRead(path);
        var stamp = LocalFiles.Stamp(stream);
        File.Move(path, moved); File.WriteAllText(path, "a different, longer file");   // allowed: the viewer shares rename and delete
        LocalFiles.ThrowIfChanged(stream, stamp);                                      // the opened file is unchanged
        Check(new StreamReader(stream).ReadToEnd() == "original");
    });
    Test("Wrong extension content rejected", () => { string path = Path.Combine(root, "fake.txt"); File.WriteAllText(path, "%PDF-1.7"); Throws<DocumentException>(() => TextFiles.Load(path)); });
    Test("Empty text is valid", () => { string path = Path.Combine(root, "empty.txt"); File.WriteAllText(path, ""); Check(TextFiles.Load(path).Text == ""); });
    Test("CSV preview explicitly reports truncation", () => { string path = Path.Combine(root, "large.csv"); File.WriteAllLines(path, Enumerable.Range(0, 1500).Select(i => i + ",001")); var view = TextFiles.Load(path); Check(view.Rows.Count == 1000 && view.Notice.Contains("1,000")); });
    // Row store: CSV and large-text rows on disk, written by the low-integrity worker and read by the app.
    string NewFolder(string name) { string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
    Test("Row store keeps text, empty fields and ragged rows", () => {
        string folder = NewFolder("store-round-trip");
        using (var writer = new RowStoreWriter(folder, "rows")) { writer.Add(["001", "Ali, A", "line1\nline2"]); writer.Add([]); writer.Add(["", "سنڌي 中文 😀"]); writer.Complete(); }
        using var store = RowStore.Open(folder, "rows");
        var rows = store.Read(0, 3);
        Check(store.Count == 3 && store.Columns == 3 && rows[0][1] == "Ali, A" && rows[0][2] == "line1\nline2" && rows[1].Length == 0 && rows[2][1] == "سنڌي 中文 😀");
        Check(store.Read(2, 1)[0][0] == "");
        Throws<ArgumentOutOfRangeException>(() => store.Read(2, 2)); });
    Test("Damaged row stores are refused", () => {
        string folder = NewFolder("store-damaged");
        using (var writer = new RowStoreWriter(folder, "rows")) { writer.Add(["a", "b"]); writer.Add(["c"]); writer.Complete(); }
        string index = Path.Combine(folder, "rows.index"), data = Path.Combine(folder, "rows.rows");
        byte[] goodIndex = File.ReadAllBytes(index), goodData = File.ReadAllBytes(data);
        void Refused(Action damage) { File.WriteAllBytes(index, goodIndex); File.WriteAllBytes(data, goodData); damage(); Throws<InvalidDataException>(() => { using var store = RowStore.Open(folder, "rows"); store.Read(0, store.Count); }); }
        Refused(() => { var b = (byte[])goodIndex.Clone(); BitConverter.GetBytes(long.MaxValue).CopyTo(b, 24); File.WriteAllBytes(index, b); });   // offset past the end
        Refused(() => { var b = (byte[])goodIndex.Clone(); BitConverter.GetBytes(0L).CopyTo(b, 32); File.WriteAllBytes(index, b); });             // offsets going backwards
        Refused(() => File.WriteAllBytes(index, goodIndex[..^4]));                                                                            // truncated index
        Refused(() => { var b = (byte[])goodIndex.Clone(); b[0] = (byte)'X'; File.WriteAllBytes(index, b); });                              // wrong header
        Refused(() => { var d = (byte[])goodData.Clone(); d[1] = 100; File.WriteAllBytes(data, d); }); });                                  // field longer than its row
    Test("CSV rows stream to a row store and match the in-memory reader", () => {
        string folder = NewFolder("store-csv"), file = Path.Combine(FindCorpus(), "complex.csv");
        var stored = TextFiles.Load(file, "Auto", "Auto", folder); var memory = TextFiles.Load(file);
        using var store = RowStore.Open(folder, stored.Store);
        Check(stored.Kind == "csv" && stored.Store == "rows" && stored.RowCount == memory.Rows.Count && store.Read(0, store.Count).Zip(memory.Rows).All(pair => pair.First.SequenceEqual(pair.Second))); });
    Test("Large text streams to a row store, one row per line", () => {
        string folder = NewFolder("store-text"), file = Path.Combine(root, "big.txt");
        File.WriteAllText(file, "﻿first line\r\n" + string.Concat(Enumerable.Range(0, 400_000).Select(i => $"line {i:D6}\n")) + "last line");
        var view = TextFiles.Load(file, "Auto", "Auto", folder);
        using var store = RowStore.Open(folder, "rows");
        Check(view.Kind == "lines" && view.RowCount == 400_002 && store.Read(0, 1)[0][0] == "first line" && store.Read(400_001, 1)[0][0] == "last line"); });
    string largeCsv = Path.Combine(FindCorpus(), "generated", "csv-large-200mb.csv");
    if (File.Exists(largeCsv))
        Test("200 MB CSV fixture streams to a row store", () => {
            string folder = NewFolder("store-large");
            var view = TextFiles.Load(largeCsv, "Auto", "Auto", folder);
            using var store = RowStore.Open(folder, "rows");
            Check(view.RowCount > 4_000_000 && view.Columns == 6 && store.Read(view.RowCount - 1, 1)[0][5] == "Hello last row"); });
    else Console.WriteLine("SKIP 200 MB CSV fixture (run npm run generate:large in tests/corpus/generate)");
    // The worker and LibreOffice run at low integrity: they must not be able to write the user's folders.
    Test("Low integrity blocks writes to the user's folders but not to its own", () => {
        string medium = Path.Combine(root, "medium"), low = Path.Combine(LowIntegrity.Root, "Temp", "probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(medium); Directory.CreateDirectory(low);
        try
        {
            string self = Environment.ProcessPath!;
            var start = new System.Diagnostics.ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            foreach (var argument in new[] { "--low-integrity-probe", medium, low }) start.ArgumentList.Add(argument);
            using var probe = System.Diagnostics.Process.Start(start)!;
            string result = probe.StandardOutput.ReadToEnd().Trim(); probe.WaitForExit();
            Check(result == "False True" && !File.Exists(Path.Combine(medium, "probe.txt")) && File.Exists(Path.Combine(low, "probe.txt")));
        }
        finally { Directory.Delete(low, true); }
    });
    // PDF snapshot. PDF.js parses inside WebView2; these cover the host-side checks only.
    Test("PDF snapshot copies bytes and leaves the original unchanged", () => {
        string path = Path.Combine(root, "doc.pdf"); File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n"));
        byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var names = Directory.GetFiles(root);
        Check(PdfFiles.Snapshot(path).AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))); Check(names.SequenceEqual(Directory.GetFiles(root))); });
    Test("PDF header accepted after leading bytes, rejected after 1024", () => {
        Check(PdfFiles.HasPdfHeader(Encoding.ASCII.GetBytes(new string(' ', 500) + "%PDF-1.4")));
        Check(!PdfFiles.HasPdfHeader(Encoding.ASCII.GetBytes(new string(' ', 1100) + "%PDF-1.4"))); });
    Test("PDF empty and mislabelled files give clear errors", () => {
        string empty = Path.Combine(root, "empty.pdf"); File.WriteAllBytes(empty, []);
        string text = Path.Combine(root, "text.pdf"); File.WriteAllText(text, "not a pdf");
        try { PdfFiles.Snapshot(empty); throw new Exception("no error"); } catch (DocumentException ex) { Check(ex.Message.Contains("empty")); }
        try { PdfFiles.Snapshot(text); throw new Exception("no error"); } catch (DocumentException ex) { Check(ex.Message.Contains("not a PDF")); } });
    Test("PDF can be read while another program has it open for writing", () => {
        string path = Path.Combine(root, "open.pdf"); File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Check(PdfFiles.Snapshot(path).Length == 9); });
    Test("PDF network path rejected before access", () => Throws<DocumentException>(() => PdfFiles.Snapshot(@"\\nonexistent.invalid\share\secret.pdf")));

    // Spreadsheets: every xlsx/xlsm fixture in tests/corpus/manifest.json is checked against its expected result.
    string corpus = FindCorpus();
    var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    // Large sheets: past 10,000 rows the reader streams every row to a row store (first rows stay in the view).
    string Workbook(string name, IEnumerable<string> rows, string after = "")
    {
        string path = Path.Combine(root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string entry, string xml) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write(xml); }
        Part("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Big\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Part("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + string.Concat(rows) + "</sheetData>" + after + "</worksheet>");
        return path;
    }
    string Cell(string reference, string text) => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";
    Test("Workbook sheets past 10,000 rows stream to a row store", () => {
        var rows = Enumerable.Range(1, 12_000).Select(n => n == 11_000 ? $"<row r=\"{n}\" hidden=\"1\">{Cell($"A{n}", "hidden")}</row>"
            : n == 11_500 ? "" : $"<row r=\"{n}\">{Cell($"A{n}", $"r{n}")}{(n == 12_000 ? Cell($"C{n}", "Hello end") : "")}</row>");
        string file = Workbook("big.xlsx", rows, "<mergeCells count=\"1\"><mergeCell ref=\"A11990:B11991\"/></mergeCells>");
        string folder = NewFolder("store-sheet");
        var sheet = Spreadsheets.Load(file, culture, folder).Sheets[0];
        using var store = RowStore.Open(folder, sheet.Store);
        var last = store.Read(11_999, 1)[0];
        Check(sheet.Store == "sheet0" && sheet.RowCount == 12_000 && store.Count == 12_000 && sheet.Rows.Count == 10_000);
        Check(store.Read(9_999, 1)[0][1] == "r10000" && store.Read(11_499, 1)[0].Length == 1 && last[0] == "lll" && last[1] == "r12000" && last[3] == "Hello end");
        Check(sheet.HiddenRows.Contains(11_000) && sheet.Merges.Any(m => m.SequenceEqual(new[] { 11_989, 0, 11_990, 1 })));
        Check(string.IsNullOrEmpty(Spreadsheets.Load(file, culture).Sheets[0].Store));   // without a folder: the 10,000-row preview as before
    });
    Test("Rows of a large sheet out of order are refused", () => {
        var rows = Enumerable.Range(1, 10_002).Select(n => $"<row r=\"{(n == 10_002 ? 10_001 : n)}\">{Cell($"A{n}", "x")}</row>");
        Throws<DocumentException>(() => Spreadsheets.Load(Workbook("unordered.xlsx", rows), culture, NewFolder("store-unordered"))); });
    string largeXlsx = Path.Combine(corpus, "generated", "xlsx-large-500k-rows.xlsx");
    if (File.Exists(largeXlsx))
        Test("500,000-row workbook fixture streams to a row store", () => {
            string folder = NewFolder("store-large-xlsx");
            var sheet = Spreadsheets.Load(largeXlsx, culture, folder).Sheets[0];
            using var store = RowStore.Open(folder, sheet.Store);
            Check(sheet.RowCount == 500_001 && store.Read(500_000, 1)[0][6] == "Hello last row"); });
    else Console.WriteLine("SKIP 500,000-row workbook fixture (run npm run generate:large in tests/corpus/generate)");
    using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "manifest.json")));
    var errorWords = new Dictionary<string, string[]> {
        ["damaged"] = ["damaged", "too large to open safely"], ["empty"] = ["empty"], ["password"] = ["password", "DRM"],
        ["mismatch"] = ["not an Excel workbook", "older Excel file", "contents are not", "older Office file", "binary data", "do not match"],
        ["unsupported"] = ["not supported", "cannot be opened in this version"], ["too-large"] = ["more than this viewer can"], ["rename"] = ["Rename it"] };

    // Word and PowerPoint: OfficePackages.Prepare must refuse bad packages with the right message and write a
    // copy of good ones with no outside references (other than hyperlinks) and no content-fetching field codes.
    foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        string format = fixture.GetProperty("format").GetString()!;
        if (!OfficePackages.IsOfficeDocument("x." + format) || fixture.TryGetProperty("generated", out _)) continue;
        string file = fixture.GetProperty("file").GetString()!;
        var expect = fixture.GetProperty("expect");
        Test("Office preparation " + file, () => {
            string path = Path.Combine(corpus, file.Replace('/', Path.DirectorySeparatorChar));
            string output = Path.Combine(root, Guid.NewGuid().ToString("N"), "document" + Path.GetExtension(path));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var siblings = Directory.GetFiles(Path.GetDirectoryName(path)!);
            if (expect.GetProperty("result").GetString() == "error")
            {
                string message = "";
                try { OfficePackages.Prepare(path, output); } catch (DocumentException ex) { message = ex.Message; }
                var words = errorWords[expect.GetProperty("error").GetString()!];
                if (!words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) throw new Exception($"Expected a {string.Join("/", words)} message, got: '{message}'");
                Check(!File.Exists(output));
            }
            else
            {
                var view = OfficePackages.Prepare(path, output);
                Check(view.Kind == (OfficePackages.IsWord(path) ? "word" : "slides"));
                using var zip = ZipFile.OpenRead(output);
                // The copy is always a plain document: no macro project, no template/show/macro-enabled main type.
                if (zip.Entries.Any(e => OfficePackages.IsMacroPart(e.FullName))) throw new Exception("The prepared copy still contains a macro project.");
                using (var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open()))
                    if (System.Text.RegularExpressions.Regex.IsMatch(types.ReadToEnd(), "macroEnabled|vbaProject|template\\.main|slideshow\\.main", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        throw new Exception("The prepared copy's content types still name a macro, template or show part.");
                if (expect.TryGetProperty("macrosRemoved", out _)) Check(view.Notice.Contains("macros", StringComparison.Ordinal) && view.Notice.Contains("never ran"));
                else Check(!view.Notice.Contains("macros"));
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
                {
                    using var reader = new StreamReader(entry.Open()); string xml = reader.ReadToEnd();
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, "<Relationship [^>]*TargetMode=\"External\"[^>]*>"))
                        if (!m.Value.Contains("/hyperlink\"")) throw new Exception($"{entry.FullName} still has an outside reference: {m.Value}");
                    if (System.Text.RegularExpressions.Regex.IsMatch(xml, "INCLUDEPICTURE|INCLUDETEXT|DDEAUTO", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        throw new Exception($"{entry.FullName} still has a content-fetching field.");
                }
                if (fixture.GetProperty("category").GetString() == "attack") Check(view.Notice.Contains("removed"));
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(siblings.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(path)!)));
        });
    }
    foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        string format = fixture.GetProperty("format").GetString()!;
        if (!(Spreadsheets.IsWorkbook("x." + format) || LegacySpreadsheets.Handles("x." + format)) || fixture.TryGetProperty("generated", out _)) continue;
        string file = fixture.GetProperty("file").GetString()!;
        var expect = fixture.GetProperty("expect");
        Test("Spreadsheet fixture " + file, () => {
            string path = Path.Combine(corpus, file.Replace('/', Path.DirectorySeparatorChar));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var siblings = Directory.GetFiles(Path.GetDirectoryName(path)!);
            if (expect.GetProperty("result").GetString() == "error")
            {
                string message = "";
                try { if (LegacySpreadsheets.Handles(path)) LegacySpreadsheets.Load(path, culture); else Spreadsheets.Load(path, culture); } catch (DocumentException ex) { message = ex.Message; }
                var words = errorWords[expect.GetProperty("error").GetString()!];
                if (!words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) throw new Exception($"Expected a {string.Join("/", words)} message, got: '{message}'");
            }
            else
            {
                var view = LegacySpreadsheets.Handles(path) ? LegacySpreadsheets.Load(path, culture) : Spreadsheets.Load(path, culture);
                var names = view.Sheets.Select(s => s.Name).ToArray();
                var expected = expect.GetProperty("sheets").EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (!names.SequenceEqual(expected)) throw new Exception($"Sheets: got {string.Join(",", names)}");
                if (expect.TryGetProperty("cells", out var cells))
                    foreach (var cell in cells.EnumerateArray())
                    {
                        var sheet = view.Sheets.Single(s => s.Name == cell.GetProperty("sheet").GetString());
                        Check(Spreadsheets.TryCell(cell.GetProperty("ref").GetString()!, out int row, out int column));
                        string actual = row - 1 < sheet.Rows.Count && column < sheet.Rows[row - 1].Length ? sheet.Rows[row - 1][column] : "(missing)";
                        if (actual != cell.GetProperty("text").GetString()) throw new Exception($"{sheet.Name}!{cell.GetProperty("ref").GetString()}: got '{actual}', expected '{cell.GetProperty("text").GetString()}'");
                    }
                if (expect.TryGetProperty("notice", out var notice)) Check(view.Sheets[0].Notice == notice.GetString());
                if (expect.TryGetProperty("columnCount", out var columnCount) && view.Sheets[0].ColumnWidths.Count != columnCount.GetInt32()) throw new Exception($"{view.Sheets[0].ColumnWidths.Count} columns, expected {columnCount}");
                if (expect.TryGetProperty("workbookNotice", out var workbookNotice)) Check(view.Notice.Contains(workbookNotice.GetString()!, StringComparison.Ordinal));
                Check(expect.TryGetProperty("macrosRemoved", out _) == view.Notice.Contains("macros"));
                // Long sheets: with a store folder every row streams to the row store; the last row is read back from it.
                if (expect.TryGetProperty("storedRows", out var storedRows))
                {
                    string folder = NewFolder("store-" + Path.GetFileName(path));
                    var big = (LegacySpreadsheets.Handles(path) ? LegacySpreadsheets.Load(path, culture, folder) : Spreadsheets.Load(path, culture, folder)).Sheets[0];
                    using var rowStore = RowStore.Open(folder, big.Store);
                    Check(big.RowCount == storedRows.GetInt32() && rowStore.Count == storedRows.GetInt32() && big.Rows.Count <= Spreadsheets.MaxRowsPerSheet);
                    string last = rowStore.Read(rowStore.Count - 1, 1)[0][1];
                    if (last != expect.GetProperty("lastRow").GetString()) throw new Exception($"Last stored row: got '{last}'");
                }
                if (expect.TryGetProperty("hiddenSheetsNotShown", out var hidden))
                    Check(hidden.EnumerateArray().All(h => !names.Contains(h.GetString())) && !view.Sheets.SelectMany(s => s.Rows).SelectMany(r => r).Any(t => t.Contains("hidden value")));
                if (expect.TryGetProperty("frozen", out var frozen))
                {
                    var sheet = view.Sheets.Single(s => s.Name == frozen.GetProperty("sheet").GetString());
                    Check(sheet.FrozenRows == frozen.GetProperty("rows").GetInt32() && sheet.FrozenColumns == frozen.GetProperty("columns").GetInt32());
                }
                if (expect.TryGetProperty("merges", out var merges))
                    foreach (var merge in merges.EnumerateArray())
                    {
                        Check(Spreadsheets.TryRange(merge.GetProperty("range").GetString()!, out var range));
                        Check(view.Sheets.Single(s => s.Name == merge.GetProperty("sheet").GetString()).Merges.Any(m => m.SequenceEqual(range)));
                    }
                // Styles, alignment and hidden columns of the first sheet, as the grid page receives them.
                (string Align, int Style) Layout(string reference)
                {
                    Check(Spreadsheets.TryCell(reference, out int row, out int column));
                    string[] parts = view.Sheets[0].Align[row - 1].Split('|');
                    return (parts[0][column].ToString(), parts.Length > 1 ? int.Parse(parts[1].Split('.')[column]) : 0);
                }
                var camel = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
                if (expect.TryGetProperty("styles", out var styles))
                    foreach (var item in styles.EnumerateArray())
                    {
                        string reference = item.GetProperty("ref").GetString()!;
                        var actual = System.Text.Json.JsonSerializer.SerializeToElement(view.CellStyles[Layout(reference).Style], camel);
                        foreach (var property in item.EnumerateObject().Where(p => p.Name != "ref"))
                        {
                            // null: the cell must not have that property.
                            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Null)
                            {
                                if (actual.TryGetProperty(property.Name, out var present)) throw new Exception($"{reference} {property.Name}: got {present}, expected none");
                                continue;
                            }
                            if (!actual.TryGetProperty(property.Name, out var got)) throw new Exception($"{reference} {property.Name}: missing, expected {property.Value}");
                            bool same = property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? got.ValueKind == System.Text.Json.JsonValueKind.Number && Math.Abs(got.GetDouble() - property.Value.GetDouble()) < 0.01
                                : got.ToString() == property.Value.ToString();
                            if (!same) throw new Exception($"{reference} {property.Name}: got {got}, expected {property.Value}");
                        }
                    }
                if (expect.TryGetProperty("align", out var aligns))
                    foreach (var item in aligns.EnumerateArray())
                        if (Layout(item.GetProperty("ref").GetString()!).Align != item.GetProperty("align").GetString()) throw new Exception($"{item.GetProperty("ref")} alignment");
                if (expect.TryGetProperty("hiddenColumns", out var hiddenColumns))
                    foreach (var letter in hiddenColumns.EnumerateArray())
                    { Check(Spreadsheets.TryCell(letter.GetString() + "1", out _, out int column)); Check(view.Sheets[0].ColumnWidths[column] == 0); }
                // Row heights of the first sheet, in points: its default and the rows that differ.
                if (expect.TryGetProperty("defaultRowHeight", out var defaultHeight) && Math.Abs(view.Sheets[0].DefaultRowHeight - defaultHeight.GetDouble()) > 0.1)
                    throw new Exception($"Default row height: got {view.Sheets[0].DefaultRowHeight}");
                if (expect.TryGetProperty("rowHeights", out var heights))
                    foreach (var item in heights.EnumerateArray())
                    {
                        int number = item.GetProperty("row").GetInt32();
                        if (!view.Sheets[0].RowHeights.TryGetValue(number, out double points) || Math.Abs(points - item.GetProperty("points").GetDouble()) > 0.5)
                            throw new Exception($"Row {number} height: got {(view.Sheets[0].RowHeights.TryGetValue(number, out double got) ? got : view.Sheets[0].DefaultRowHeight)}");
                    }
                if (expect.TryGetProperty("rightToLeft", out var rtl))
                    Check(rtl.EnumerateArray().All(n => view.Sheets.Single(s => s.Name == n.GetString()).RightToLeft));
                // Pictures (written to the work folder under checked names) and charts ("type:title:series").
                if (expect.TryGetProperty("drawings", out var drawings))
                {
                    string folder = NewFolder("media-" + Path.GetFileName(path));
                    var drawn = LegacySpreadsheets.Handles(path) ? LegacySpreadsheets.Load(path, culture, folder) : Spreadsheets.Load(path, culture, folder);
                    foreach (var item in drawings.EnumerateArray())
                    {
                        int index = drawn.Sheets.FindIndex(s => s.Name == item.GetProperty("sheet").GetString());
                        var sheet = drawn.Sheets[index];
                        var media = sheet.Pictures.Where(p => p.Chart is null && p.Shape is null).ToList();
                        if (item.TryGetProperty("pictures", out var count) && media.Count != count.GetInt32()) throw new Exception($"{sheet.Name}: {media.Count} pictures");
                        foreach (var picture in media)
                            Check(ImageFiles.ContentTypeOf(picture.Media) == "image/png" && picture.Media.StartsWith($"media-{index}-") && File.Exists(Path.Combine(folder, picture.Media)));
                        if (item.TryGetProperty("charts", out var charts))
                        {
                            var got = sheet.Pictures.Where(p => p.Chart is not null).Select(p => $"{p.Chart!.Type}:{p.Chart.Title}:{p.Chart.Series.Count}").ToArray();
                            var wanted = charts.EnumerateArray().Select(c => c.GetString()!).ToArray();
                            if (!got.SequenceEqual(wanted)) throw new Exception($"{sheet.Name} charts: got {string.Join(", ", got)}");
                            Check(sheet.Pictures.Where(p => p.Chart is not null).All(p => p.Chart!.Categories.Count > 0 && p.Chart.Series.All(s => s.Values.Count > 0 && s.Values.All(v => v is not null))));
                        }
                        Check(item.TryGetProperty("chartSheet", out _) == sheet.ChartSheet);
                        // Shapes as "geometry:fill:line:paragraph/paragraph" ("-" for none).
                        if (item.TryGetProperty("shapes", out var shapes))
                        {
                            var got = sheet.Pictures.Where(p => p.Shape is not null)
                                .Select(p => $"{p.Shape!.Geometry}:{p.Shape.Fill ?? "-"}:{p.Shape.Line ?? "-"}:{string.Join("/", p.Shape.Paragraphs.Select(x => x.Text))}").ToArray();
                            var wanted = shapes.EnumerateArray().Select(s => s.GetString()!).ToArray();
                            if (!got.SequenceEqual(wanted)) throw new Exception($"{sheet.Name} shapes: got {string.Join(" | ", got)}");
                            // Boxes of grouped shapes as fractions of their anchor ("x,y,width,height").
                            if (item.TryGetProperty("parts", out var parts))
                            {
                                var gotParts = sheet.Pictures.Where(p => p.Part is not null).Select(p => string.Join(",", p.Part!.Select(v => Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
                                if (!gotParts.SequenceEqual(parts.EnumerateArray().Select(s => s.GetString()!))) throw new Exception($"{sheet.Name} parts: got {string.Join(" | ", gotParts)}");
                            }
                        }
                        // Series values and categories, as saved, of the first ("values") and second ("values2") chart.
                        foreach (var (suffix, n) in new[] { ("", 0), ("2", 1) })
                            if (item.TryGetProperty("values" + suffix, out var values))
                            {
                                var chart = sheet.Pictures.Where(p => p.Chart is not null).ElementAt(n).Chart!;
                                var got = chart.Series.Select(s => string.Join(",", s.Values.Select(v => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"))).ToArray();
                                var wanted = values.EnumerateArray().Select(v => v.GetString()!).ToArray();
                                if (!got.SequenceEqual(wanted)) throw new Exception($"{sheet.Name} chart {n + 1} values: got {string.Join(" | ", got)}");
                                // Axis titles ("category|value") and the first series' data labels ("|" between points).
                                if (item.TryGetProperty("titles" + suffix, out var titles) && $"{chart.CategoryTitle}|{chart.ValueTitle}" != titles.GetString())
                                    throw new Exception($"{sheet.Name} chart {n + 1} axis titles: got {chart.CategoryTitle}|{chart.ValueTitle}");
                                if (item.TryGetProperty("labels" + suffix, out var pointLabels) && string.Join("|", chart.Series[0].PointLabels) != pointLabels.GetString())
                                    throw new Exception($"{sheet.Name} chart {n + 1} labels: got {string.Join("|", chart.Series[0].PointLabels)}");
                                if (item.TryGetProperty("categories" + suffix, out var categories) && string.Join(",", chart.Categories) != categories.GetString())
                                    throw new Exception($"{sheet.Name} chart {n + 1} categories: got {string.Join(",", chart.Categories)}");
                            }
                    }
                    if (fixture.GetProperty("category").GetString() == "attack") Check(drawn.Notice.Contains("linked picture"));
                }
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(siblings.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(path)!)));
        });
    }
    // Pictures and plain-text data files: every fixture in the manifest, with the originals left untouched.
    foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        string file = fixture.GetProperty("file").GetString()!;
        bool picture = ImageFiles.IsImage(file), data = (TextFiles.PlainExtensions.Contains(Path.GetExtension(file)) && !file.EndsWith(".txt")) || file.EndsWith(".tsv");
        if (!(picture || data) || fixture.TryGetProperty("generated", out _)) continue;
        var expect = fixture.GetProperty("expect");
        Test((picture ? "Picture fixture " : "Data fixture ") + file, () => {
            string path = Path.Combine(corpus, file.Replace('/', Path.DirectorySeparatorChar));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var siblings = Directory.GetFiles(Path.GetDirectoryName(path)!);
            // "stage: renderer" files pass the app's checks; the sandboxed renderer refuses them (smoke test).
            bool opensHere = expect.GetProperty("result").GetString() == "open" || expect.TryGetProperty("stage", out _);
            if (!opensHere)
            {
                string message = "";
                try { if (picture) ImageFiles.Snapshot(path); else TextFiles.Load(path); } catch (DocumentException ex) { message = ex.Message; }
                var words = errorWords[expect.GetProperty("error").GetString()!];
                if (!words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) throw new Exception($"Expected a {string.Join("/", words)} message, got: '{message}'");
            }
            else if (picture)
            {
                var image = ImageFiles.Snapshot(path);
                if (expect.TryGetProperty("format", out var imageFormat) && image.Format != imageFormat.GetString()) throw new Exception($"Format: got {image.Format}");
                if (expect.TryGetProperty("width", out var width) && width.GetInt32() > 0) Check(image.Width == width.GetInt32() && image.Height == expect.GetProperty("height").GetInt32());
                if (expect.GetProperty("result").GetString() == "open" && image.Incomplete != expect.TryGetProperty("incomplete", out _)) throw new Exception($"Incomplete: got {image.Incomplete}");
            }
            else
            {
                var view = TextFiles.Load(path);
                // Tab-separated files open in the grid, split on tabs only (their fields contain commas).
                string shown = view.Kind == "csv" && file.EndsWith(".tsv") && view.Delimiter == '\t' && view.Rows.All(row => row.Length == view.Rows[0].Length)
                    ? string.Join("\n", view.Rows.Select(row => string.Join("\t", row))) : view.Kind == "text" ? view.Text : throw new Exception($"Shown as {view.Kind}");
                foreach (var text in expect.GetProperty("text").EnumerateArray())
                    if (!shown.Contains(text.GetString()!)) throw new Exception($"Text '{text.GetString()}' not shown.");
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(siblings.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(path)!)));
        });
    }
    // Web pages, web archives and books (0.8.0): the worker's part. What the page removes is checked by the smoke tests.
    foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        string file = fixture.GetProperty("file").GetString()!;
        if (!WebDocuments.Handles(file) || fixture.TryGetProperty("generated", out _)) continue;
        var expect = fixture.GetProperty("expect");
        Test("Web fixture " + file, () => {
            string path = Path.Combine(corpus, file.Replace('/', Path.DirectorySeparatorChar));
            string work = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var siblings = Directory.GetFiles(Path.GetDirectoryName(path)!);
            if (expect.GetProperty("result").GetString() == "error")
            {
                string message = "";
                try { WebDocuments.Load(path, work); } catch (DocumentException ex) { message = ex.Message; }
                var words = errorWords[expect.GetProperty("error").GetString()!];
                if (!words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) throw new Exception($"Expected a {string.Join("/", words)} message, got: '{message}'");
            }
            else
            {
                var view = WebDocuments.Load(path, work);
                Check(view.Kind == "web" && view.Store == WebDocuments.Output);
                var content = System.Text.Json.JsonSerializer.Deserialize<WebDocuments.Content>(File.ReadAllText(Path.Combine(work, WebDocuments.Output)), new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })!;
                if (expect.TryGetProperty("parts", out var parts) && content.Parts.Count != parts.GetInt32()) throw new Exception($"Parts: {content.Parts.Count}");
                if (expect.TryGetProperty("pictures", out var pictures) && content.Pictures.Values.Distinct().Count() != pictures.GetInt32()) throw new Exception($"Pictures: {content.Pictures.Count}");
                if (expect.TryGetProperty("styles", out var styles) && content.Styles.Values.Distinct().Count() != styles.GetInt32()) throw new Exception($"Style sheets: {content.Styles.Count}");
                string all = string.Join("\n", content.Parts.Select(p => p.Html));
                foreach (var text in expect.GetProperty("text").EnumerateArray())
                    if (!all.Contains(text.GetString()!)) throw new Exception($"Text '{text.GetString()}' not found.");
                // Pictures written by the worker are the files the page may ask for, each still a picture of its named type.
                foreach (var name in content.Pictures.Values.Distinct())
                    Check(ImageFiles.ContentTypeOf(name) is { } type && ImageFiles.Identify(File.ReadAllBytes(Path.Combine(work, name)))?.ContentType == type);
                // Nothing outside the file was written: only web.json and the pictures.
                Check(Directory.GetFiles(work).Select(Path.GetFileName).All(name => name == WebDocuments.Output || ImageFiles.ContentTypeOf(name!) is not null));
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(siblings.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(path)!)));
        });
    }
    // Password-protected Office files (Agile encryption, made by officecrypto-tool with the test password "viewer-test").
    T WithPassword<T>(string? password, Func<T> open) { OfficeEncryption.Password = password; try { return open(); } finally { OfficeEncryption.Password = null; } }
    Test("Protected workbook: asks, refuses a wrong password, opens with the right one", () =>
    {
        string path = Path.Combine(corpus, "xlsx", "password.xlsx");
        var asked = Throws<PasswordException>(() => Spreadsheets.Load(path));
        Check(!asked.Incorrect && asked.Message.Contains("Enter its password"));
        Check(Throws<PasswordException>(() => WithPassword("Viewer-test", () => Spreadsheets.Load(path))).Incorrect);
        var opened = WithPassword("viewer-test", () => Spreadsheets.Load(path));
        var plain = Spreadsheets.Load(Path.Combine(corpus, "xlsx", "simple.xlsx"));
        Check(opened.Kind == "sheet" && opened.Sheets.Count == plain.Sheets.Count && opened.Sheets[0].Rows[0].SequenceEqual(plain.Sheets[0].Rows[0]));
    });
    foreach (var name in new[] { "docx/password.docx", "pptx/password.pptx" })
        Test("Protected Office file opens with its password: " + name, () =>
        {
            string path = Path.Combine(corpus, name.Replace('/', Path.DirectorySeparatorChar));
            string output = Path.Combine(root, Guid.NewGuid().ToString("N"), "document" + Path.GetExtension(path));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            Check(!Throws<PasswordException>(() => OfficePackages.Prepare(path, output)).Incorrect && !File.Exists(output));
            Check(Throws<PasswordException>(() => WithPassword("wrong", () => OfficePackages.Prepare(path, output))).Incorrect && !File.Exists(output));
            var view = WithPassword("viewer-test", () => OfficePackages.Prepare(path, output));
            using var zip = new ZipArchive(File.OpenRead(output));
            Check(zip.GetEntry(name.StartsWith("docx") ? "word/document.xml" : "ppt/presentation.xml") is not null);
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
        });
    // Older formats, saved with the test password by LibreOffice (scripts/make-protected-office.ps1): RC4 encryption.
    Test("Protected .xls workbook (RC4): asks, refuses a wrong password, opens with the right one", () =>
    {
        string path = Path.Combine(corpus, "xls", "password.xls");
        Check(!Throws<PasswordException>(() => LegacySpreadsheets.Load(path)).Incorrect);
        Check(Throws<PasswordException>(() => WithPassword("Viewer-test", () => LegacySpreadsheets.Load(path))).Incorrect);
        var opened = WithPassword("viewer-test", () => LegacySpreadsheets.Load(path));
        var plain = LegacySpreadsheets.Load(Path.Combine(corpus, "xls", "simple.xls"));
        Check(opened.Sheets.Select(s => s.Name).SequenceEqual(plain.Sheets.Select(s => s.Name)));
        for (int s = 0; s < plain.Sheets.Count; s++)
            Check(opened.Sheets[s].Rows.Count == plain.Sheets[s].Rows.Count && opened.Sheets[s].Rows.Zip(plain.Sheets[s].Rows).All(pair => pair.First.SequenceEqual(pair.Second)));
    });
    Test("Protected .doc (RC4): asks, refuses a wrong password, gives a decrypted cleaned copy with the right one", () =>
    {
        string path = Path.Combine(corpus, "doc", "password.doc");
        string Output() => Path.Combine(root, Guid.NewGuid().ToString("N"), "document.doc");
        Check(!Throws<PasswordException>(() => ConvertedDocuments.Prepare(path, Output())).Incorrect);
        Check(Throws<PasswordException>(() => WithPassword("wrong", () => ConvertedDocuments.Prepare(path, Output()))).Incorrect);
        string output = Output();
        WithPassword("viewer-test", () => ConvertedDocuments.Prepare(path, output));
        var copy = new CompoundFile(File.ReadAllBytes(output), "test");
        byte[] word = copy.Read(copy.Find("WordDocument")!, "test");
        Check((BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A)) & 0x0100) == 0);
        // The decrypted main text holds the document's words (stored as single-byte text by LibreOffice).
        Check(Encoding.Latin1.GetString(word).Contains("Hello from a Word document") || Encoding.Unicode.GetString(word).Contains("Hello from a Word document"));
    });
    Test("BLAKE2b and Argon2id match the RFC test vectors", () =>
    {
        Check(Convert.ToHexString(Blake2b.Hash("abc"u8.ToArray(), 64)).Equals(
            "BA80A53F981C4D0D6A2797B69F12F6E94C212F14685AC4B74B12BB6FDBFFA2D17D87C5392AAB792DC252D5DE4533CC9518D38AA8DBF1925AB92386EDD4009923", StringComparison.Ordinal));
        byte[] tag = Argon2.Hash(Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray(), 3, 32, 4, 32,
            Enumerable.Repeat((byte)3, 8).ToArray(), Enumerable.Repeat((byte)4, 12).ToArray());
        Check(Convert.ToHexString(tag) == "0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659");
    });
    foreach (var name in new[] { "odt/password-libreoffice.odt", "ods/password.ods", "odp/password.odp", "odt/password-odf12.odt", "ods/password-odf12.ods" })
        Test("Protected OpenDocument file decrypts with its password: " + name, () =>
        {
            using var zip = new ZipArchive(File.OpenRead(Path.Combine(corpus, name.Replace('/', Path.DirectorySeparatorChar))));
            Check(OpenDocumentEncryption.IsEncrypted(zip));
            Check(!Throws<PasswordException>(() => OpenDocumentEncryption.Decrypt(zip, "file")).Incorrect);
            Check(Throws<PasswordException>(() => WithPassword("Viewer-test", () => OpenDocumentEncryption.Decrypt(zip, "file"))).Incorrect);
            byte[] plain = WithPassword("viewer-test", () => OpenDocumentEncryption.Decrypt(zip, "file"));
            using var inner = new ZipArchive(new MemoryStream(plain));
            using var content = new StreamReader(inner.GetEntry("content.xml")!.Open());
            Check(content.ReadToEnd().Contains("Hello"));
        });
    Test("Protected OpenDocument files open through the usual readers with their password", () =>
    {
        foreach (var name in new[] { "odt/password-libreoffice.odt", "odp/password.odp", "odt/password-odf12.odt" })
        {
            string path = Path.Combine(corpus, name.Replace('/', Path.DirectorySeparatorChar)), output = Path.Combine(root, Guid.NewGuid().ToString("N"), "document" + Path.GetExtension(path));
            WithPassword("viewer-test", () => ConvertedDocuments.Prepare(path, output));
            using var zip = new ZipArchive(File.OpenRead(output));
            Check(!OpenDocumentEncryption.IsEncrypted(zip) && zip.GetEntry("content.xml") is not null);
        }
        var sheet = WithPassword("viewer-test", () => LegacySpreadsheets.Load(Path.Combine(corpus, "ods", "password.ods")));
        Check(sheet.Kind == "sheet" && sheet.Sheets.Count > 0 && sheet.Sheets[0].Rows.Count > 0);
    });
    Test("RC4 CryptoAPI header (Office 2003 and later .doc/.xls): parsed and the password checked (self-made header)", () =>
    {
        byte[] header = LegacyEncryption.CryptoApiHeaderForTests("Pässword 2003");
        Check(Throws<PasswordException>(() => LegacyEncryption.Open(header, "file")) is { Incorrect: false });
        Check(Throws<PasswordException>(() => WithPassword("password 2003", () => LegacyEncryption.Open(header, "file"))).Incorrect);
        Check(WithPassword("Pässword 2003", () => LegacyEncryption.Open(header, "file")) is not null);
    });
    Test("Standard (Office 2007) encryption round trip", () =>
    {
        byte[] plain = File.ReadAllBytes(Path.Combine(corpus, "xlsx", "simple.xlsx"));
        var (info, package) = OfficeEncryption.EncryptStandardForTests(plain, "Pässword 2007");
        Check(OfficeEncryption.DecryptStreamsForTests(info, package, "Pässword 2007", "workbook").SequenceEqual(plain));
        Check(Throws<PasswordException>(() => OfficeEncryption.DecryptStreamsForTests(info, package, "password 2007", "workbook")).Incorrect);
    });
    Test("Web references stay inside the book", () =>
    {
        Check(WebDocuments.Resolve("OEBPS/text/", "../images/a.png") == "OEBPS/images/a.png");
        Check(WebDocuments.Resolve("OEBPS/", "../../../../outside.png") == "outside.png");
        Check(WebDocuments.Resolve("", "a/./b/../c.xhtml#x") == "a/c.xhtml");
    });
    // Formats shown through LibreOffice: the worker's preparation must refuse what it should, and its private copy
    // must hold no address of the test listener (remote or network share) in single-byte or UTF-16 text.
    foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        string file = fixture.GetProperty("file").GetString()!;
        if (!ConvertedDocuments.Handles(file) || fixture.TryGetProperty("generated", out _)) continue;
        var expect = fixture.GetProperty("expect");
        Test("Converted-format preparation " + file, () => {
            string path = Path.Combine(corpus, file.Replace('/', Path.DirectorySeparatorChar));
            string output = Path.Combine(root, Guid.NewGuid().ToString("N"), "document" + Path.GetExtension(path));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path)); var siblings = Directory.GetFiles(Path.GetDirectoryName(path)!);
            if (expect.GetProperty("result").GetString() == "error")
            {
                string message = "";
                try { ConvertedDocuments.Prepare(path, output); } catch (DocumentException ex) { message = ex.Message; }
                var words = errorWords[expect.GetProperty("error").GetString()!];
                if (!words.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))) throw new Exception($"Expected a {string.Join("/", words)} message, got: '{message}'");
                Check(!File.Exists(output));
            }
            else
            {
                var view = ConvertedDocuments.Prepare(path, output);
                string kind = expect.GetProperty("kind").GetString()!;
                Check(view.Kind == (kind == "pages" ? "word" : kind));
                var removed = System.Text.RegularExpressions.Regex.Match(view.Notice, @"(\d+) references? to content stored outside");
                int count = removed.Success ? int.Parse(removed.Groups[1].Value) : 0;
                if (expect.TryGetProperty("removed", out var exact) && count != exact.GetInt32()) throw new Exception($"Removed {count} references, expected {exact.GetInt32()}: {view.Notice}");
                if (expect.TryGetProperty("removedAtLeast", out var least) && count < least.GetInt32()) throw new Exception($"Removed {count} references, expected at least {least.GetInt32()}");
                // Everything the copy holds, part by part: ZIP entries (decompressed) or compound-file streams.
                byte[] copy = File.ReadAllBytes(output);
                // A template's copy is labelled as the document it holds (no "-template" media type left).
                if (file.EndsWith(".ott"))
                {
                    using var package = new System.IO.Compression.ZipArchive(new MemoryStream(copy));
                    using var mime = new StreamReader(package.GetEntry("mimetype")!.Open());
                    Check(mime.ReadToEnd() == "application/vnd.oasis.opendocument.text" && ConvertedDocuments.CopyExtension(view) == ".odt");
                    using var manifestPart = new StreamReader(package.GetEntry("META-INF/manifest.xml")!.Open());
                    Check(!manifestPart.ReadToEnd().Contains("-template"));
                }
                var parts = new List<(string Name, byte[] Bytes)> { ("file", copy) };
                if (copy.AsSpan().StartsWith("PK"u8))
                    using (var zip = ZipFile.OpenRead(output))
                        foreach (var entry in zip.Entries.Where(e => e.Length > 0)) { using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); parts.Add((entry.FullName, buffer.ToArray())); }
                if (CompoundFile.IsCompoundFile(copy))
                {
                    var compound = new CompoundFile(copy, "copy");
                    parts = compound.Entries.Where(e => e.Type == 2).Select(e => (e.Name, compound.Read(e, "copy"))).ToList();
                }
                // A HYPERLINK field in the visible text is an ordinary link (the viewer asks before opening it) and stays.
                foreach (var (name, bytes) in parts.Where(p => p.Bytes.Length > 1))
                    foreach (string text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes), Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) })
                        foreach (string address in new[] { "127.0.0.1:47831", "127.0.0.1@47831" })
                            if (System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(address)).FirstOrDefault(m => !text.Substring(Math.Max(0, m.Index - 30), Math.Min(30, m.Index)).Contains("HYPERLINK")) is { } found && found.Index is int at)
                                throw new Exception($"The prepared copy still refers to the test listener, in {name}: …{System.Text.RegularExpressions.Regex.Replace(text.Substring(Math.Max(0, at - 50), Math.Min(90, text.Length - Math.Max(0, at - 50))), @"[\x00-\x1f]", "·")}…");
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(siblings.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(path)!)));
        });
    }
    Test("Older spreadsheets show the saved result, never a recalculated one", () => {
        // complex.xls B7 is =SUM(...) saved as 20; the saved result is changed to 99, which must be what is shown.
        byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, "xls", "complex.xls"));
        var compound = new CompoundFile(bytes, "file"); var entry = compound.Find("Workbook")!; var book = compound.Read(entry, "file");
        bool changed = false;
        for (int at = 0; at + 4 <= book.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 2));
            if (type == 0x0006 && BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 4)) == 6 && BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 6)) == 1)
            { BinaryPrimitives.WriteDoubleLittleEndian(book.AsSpan(at + 10), 99); changed = true; }
            at += 4 + length;
        }
        Check(changed); compound.Write(entry, book, "file");
        string stale = Path.Combine(root, "stale.xls"); File.WriteAllBytes(stale, bytes);
        Check(LegacySpreadsheets.Load(stale, culture).Sheets[0].Rows[6][1] == "$99.00");
    });
    Test("Older spreadsheets: an XF fill pattern is drawn as a pattern over the background", () => {
        // LibreOffice cannot save pattern fills, so styles.xls's C2 (a solid green fill, an empty formatted cell) has
        // its cell format changed to pattern 9 (dark grid): the green becomes the pattern colour.
        byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, "xls", "styles.xls"));
        var compound = new CompoundFile(bytes, "file"); var entry = compound.Find("Workbook")!; var book = compound.Read(entry, "file");
        int xf = -1;
        for (int at = 0; at + 4 <= book.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 2));
            if (type == 0x0201 && BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 4)) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 6)) == 2)
                xf = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 8));
            at += 4 + length;
        }
        Check(xf >= 0);
        int seen = 0; bool patched = false;
        for (int at = 0; at + 4 <= book.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at + 2));
            if (type == 0x00E0 && seen++ == xf)
            {
                uint more = BinaryPrimitives.ReadUInt32LittleEndian(book.AsSpan(at + 4 + 14));
                BinaryPrimitives.WriteUInt32LittleEndian(book.AsSpan(at + 4 + 14), (more & 0x03FFFFFF) | (9u << 26));
                patched = true;
            }
            at += 4 + length;
        }
        Check(patched); compound.Write(entry, book, "file");
        string path = Path.Combine(root, "pattern.xls"); File.WriteAllBytes(path, bytes);
        var view = LegacySpreadsheets.Load(path, culture);
        var style = view.CellStyles[int.Parse(view.Sheets[0].Align[1].Split('|')[1].Split('.')[2])];
        if (style.Pattern != "darkGrid #00b050" || style.Fill is null) throw new Exception($"C2: fill {style.Fill}, pattern {style.Pattern}");
    });
    Test("Older spreadsheets: Office Art shape types give their outlines", () => {
        // LibreOffice saves shapes.xls's ellipse as a freeform outline (type 4095, drawn as a rectangle); Excel saves an
        // ellipse as type 3. The second freeform shape record (the ellipse) is changed to type 3.
        byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, "xls", "shapes.xls"));
        var compound = new CompoundFile(bytes, "file"); var entry = compound.Find("Workbook")!; var book = compound.Read(entry, "file");
        int seen = 0; bool patched = false;
        for (int at = 0; at + 4 <= book.Length && !patched; at++)
            if (book[at] == 0xF2 && book[at + 1] == 0xFF && book[at + 2] == 0x0A && book[at + 3] == 0xF0 && ++seen == 2)
            { book[at] = 0x32; book[at + 1] = 0x00; patched = true; }
        Check(patched); compound.Write(entry, book, "file");
        string path = Path.Combine(root, "ellipse.xls"); File.WriteAllBytes(path, bytes);
        var shapes = LegacySpreadsheets.Load(path, culture).Sheets[0].Pictures.Where(p => p.Shape is not null).Select(p => p.Shape!).ToList();
        if (shapes.Count != 8 || shapes[2].Geometry != "ellipse" || shapes[2].Fill is not null || shapes[2].Line != "#c00000" || shapes[2].Dash != "dash")
            throw new Exception($"third shape: {shapes.ElementAtOrDefault(2)?.Geometry} fill {shapes.ElementAtOrDefault(2)?.Fill}");
    });
    Test("Older Office files: password, Word 95 and damaged files are refused clearly", () => {
        string Patched(string source, string name, Func<byte[], CompoundFile, bool> patch)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, source));
            var compound = new CompoundFile(bytes, "file");
            Check(patch(bytes, compound));
            string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path;
        }
        string Message(string path) { try { ConvertedDocuments.Prepare(path, Path.Combine(root, Guid.NewGuid().ToString("N"), "copy")); return ""; } catch (DocumentException ex) { return ex.Message; } }
        // Word: the FIB's "encrypted" flag; an nFib older than Word 97.
        bool Fib(CompoundFile compound, Action<byte[]> change, byte[] all)
        {
            var entry = compound.Find("WordDocument")!; var word = compound.Read(entry, "file"); change(word); compound.Write(entry, word, "file"); return true;
        }
        // Marked as encrypted without an encryption header: damaged (real protected files are tested above).
        Check(Message(Patched("doc/simple.doc", "encrypted.doc", (all, c) => Fib(c, w => w[0x0B] |= 0x01, all))).Contains("damaged"));
        Check(Message(Patched("doc/simple.doc", "word95.doc", (all, c) => Fib(c, w => BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(2), 0x0065), all))).Contains("Word 95"));
        // Excel: a FILEPASS record in the workbook stream (the second record's type is changed to it).
        string SheetMessage(string path) { try { LegacySpreadsheets.Load(path); return ""; } catch (DocumentException ex) { return ex.Message; } }
        Check(SheetMessage(Patched("xls/simple.xls", "encrypted.xls", (all, c) =>
        {
            var entry = c.Find("Workbook")!; var book = c.Read(entry, "file");
            int second = 4 + BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(2));
            BinaryPrimitives.WriteUInt16LittleEndian(book.AsSpan(second), 0x002F); c.Write(entry, book, "file"); return true;
        })) is var fake && (fake.Contains("password") || fake.Contains("damaged")));
        // A compound file cut short, and one whose FAT points outside the file.
        byte[] doc = File.ReadAllBytes(Path.Combine(corpus, "doc", "simple.doc"));
        string cut = Path.Combine(root, "cut.doc"); File.WriteAllBytes(cut, doc[..(doc.Length / 3)]);
        Check(Message(cut).Contains("damaged"));
        var broken = (byte[])doc.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(0x4c), 0x7FFFFFF0);
        string bad = Path.Combine(root, "badfat.doc"); File.WriteAllBytes(bad, broken);
        Check(Message(bad).Contains("damaged"));
    });
    Test("Picture formats are recognised by content, not by name", () => {
        byte[] ftyp(string brand) => [0, 0, 0, 24, .. "ftyp"u8, .. Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0, .. "mif1"u8, .. Encoding.ASCII.GetBytes(brand)];
        Check(ImageFiles.Identify(ftyp("avif"))?.Format == "AVIF");
        Check(ImageFiles.Identify(ftyp("heic"))?.Format == "HEIF");
        string heic = Path.Combine(root, "photo.jpg"); File.WriteAllBytes(heic, ftyp("heic"));
        Check(ImageFiles.Snapshot(heic).Format == "HEIF");                 // a HEIC photo named .jpg still goes to the HEIC path
        Check(ImageFiles.ContentTypeOf("media-0-0.heic") is null);         // never served to the page as a sheet picture
        Check(ImageFiles.Identify(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!-- note --><!DOCTYPE svg><svg xmlns=\"http://www.w3.org/2000/svg\"/>"))?.Format == "SVG");
        Check(ImageFiles.Identify(Encoding.UTF8.GetBytes("<html><body><svg></svg></body></html>")) is null);   // an HTML page is not an SVG
        string gz = Path.Combine(root, "drawing.svg"); File.WriteAllBytes(gz, [0x1f, 0x8b, 8, 0, 0, 0, 0, 0]);
        Check(Throws<DocumentException>(() => ImageFiles.Snapshot(gz)).Message.Contains("compressed"));
        Throws<DocumentException>(() => ImageFiles.Snapshot(@"\\nonexistent.invalid\share\photo.png"));
    });
    Test("Every supported type is registered by the installer, and nothing else", () => {
        string script = File.ReadAllText(Path.Combine(Path.GetDirectoryName(corpus)!, "..", "installer", "PlainViewer.iss"));
        var registered = System.Text.RegularExpressions.Regex.Matches(script, "#define Ext\\[\\d+\\] \"([a-z0-9]+)\"").Select(m => "." + m.Groups[1].Value).ToHashSet();
        var supported = Formats.All.ToHashSet();
        if (!registered.SetEquals(supported)) throw new Exception($"Installer only: {string.Join(" ", registered.Except(supported))}; app only: {string.Join(" ", supported.Except(registered))}");
        Check(Formats.OpenDialogFilter.StartsWith("All supported files|*.pdf;"));
        // The Microsoft Store package (MSIX) registers the same types.
        string manifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(corpus)!, "..", "installer", "msix", "AppxManifest.xml"));
        var packaged = System.Text.RegularExpressions.Regex.Matches(manifest, "<uap:FileType>([.a-z0-9]+)</uap:FileType>").Select(m => m.Groups[1].Value).ToList();
        if (packaged.Count != packaged.Distinct().Count() || !packaged.ToHashSet().SetEquals(supported))
            throw new Exception($"MSIX only: {string.Join(" ", packaged.Except(supported))}; app only: {string.Join(" ", supported.Except(packaged))}");
    });
    Test("Spreadsheet complex fixture reports hidden sheets and the missing formula result", () => {
        var view = Spreadsheets.Load(Path.Combine(corpus, "xlsx", "complex.xlsx"), culture);
        Check(view.Notice.Contains("2 hidden sheets") && view.Notice.Contains("1 formula cell has no saved result")); });
    Test("Spreadsheet cell references", () => {
        Check(Spreadsheets.TryCell("A1", out int r, out int c) && r == 1 && c == 0);
        Check(Spreadsheets.TryCell("AB12", out r, out c) && r == 12 && c == 27);
        Check(!Spreadsheets.TryCell("12", out _, out _) && !Spreadsheets.TryCell("ABCD1", out _, out _)); });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Results: {passed} passed, {failed} failed. No UI, Office fidelity, network instrumentation or release performance checks were run.");
return failed == 0 ? 0 : 1;

static bool CanWrite(string folder)
{
    try { File.WriteAllText(Path.Combine(folder, "probe.txt"), "probe"); return true; }
    catch (UnauthorizedAccessException) { return false; }
}

static string FindCorpus()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "tests", "corpus", "manifest.json"))) return Path.Combine(dir.FullName, "tests", "corpus");
    throw new DirectoryNotFoundException("tests/corpus/manifest.json not found above the test output folder.");
}
