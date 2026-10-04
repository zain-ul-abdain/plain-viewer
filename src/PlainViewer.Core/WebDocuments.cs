using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
namespace PlainViewer.Core;

// Web pages (.htm .html .xhtml), saved web archives (.mht .mhtml) and EPUB books (0.8.0). Runs in the worker: reads the
// file, works out its text encoding, and for archives and books collects the parts stored inside the file. Nothing a
// document refers to outside itself is opened. The result goes to the work folder as web.json (parts and style sheets)
// plus the pictures stored in the file, each identified by its bytes and named like spreadsheet pictures
// (media-0-<n>.<type>). The page (Assets/web) parses the markup with the browser's inert parser, removes scripts,
// frames, forms, embedded objects, event handlers and outside references, and shows the result in a sandboxed frame
// where scripts cannot run; WebView2's request filter blocks everything else.
public static class WebDocuments
{
    public static readonly string[] PageExtensions = [".htm", ".html", ".xhtml"];
    public static readonly string[] ArchiveExtensions = [".mht", ".mhtml"];
    public static readonly string[] BookExtensions = [".epub"];
    public static readonly string[] Extensions = [.. PageExtensions, .. ArchiveExtensions, .. BookExtensions];
    public static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public const long PageLimit = 16L * 1024 * 1024;      // a web page or web archive
    public const long BookLimit = 256L * 1024 * 1024;     // an EPUB file
    public const long TextBudget = 48L * 1024 * 1024;     // characters of markup and style sheets in one document
    public const string Output = "web.json";

    public sealed class Part
    {
        public string Name { get; set; } = "";     // its path in the book or archive ("" for a single page)
        public string Title { get; set; } = "";
        public string Html { get; set; } = "";
    }
    public sealed class Content
    {
        public string Kind { get; set; } = "page";  // "page", "archive" or "book"
        public string Title { get; set; } = "";
        public List<Part> Parts { get; set; } = [];
        public Dictionary<string, string> Pictures { get; set; } = [];   // path or address in the file -> media name
        public Dictionary<string, string> Styles { get; set; } = [];     // path or address in the file -> style sheet text
        public Dictionary<string, string> Fonts { get; set; } = [];      // books: path in the book -> font name (FontName)
    }

    public static DocumentView Load(string path, string folder)
    {
        TextFiles.ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Handles(path)) throw new DocumentException($"{extension} files do not open in this view.");
        bool book = BookExtensions.Contains(extension), archive = ArchiveExtensions.Contains(extension);
        string noun = book ? "book" : archive ? "web archive" : "web page";
        byte[] bytes;
        using (var stream = LocalFiles.OpenRead(path))
        {
            long length = stream.Length;
            var stamp = LocalFiles.Stamp(stream);
            if (length == 0) throw new DocumentException($"This {noun} is empty (0 bytes). It may not have finished downloading or copying. Get a complete copy and try again.");
            if (length > (book ? BookLimit : PageLimit))
                throw new DocumentException($"This {noun} is larger than {(book ? 256 : 16)} MB, which is more than this viewer can open safely.");
            bytes = new byte[length];
            stream.ReadExactly(bytes);
            LocalFiles.ThrowIfChanged(stream, stamp);
        }
        var budget = new SheetDrawings.Budget { Pictures = 5000, Bytes = 160L * 1024 * 1024 };
        var notes = new List<string>();
        Content content;
        string label;
        try
        {
            if (book) { content = Book(bytes, folder, budget, notes); label = "EPUB book"; }
            else if (archive) { content = Archive(bytes, folder, budget); label = "Web archive"; }
            else { content = new Content { Parts = [new Part { Html = Page(bytes) }] }; label = "Web page"; }
        }
        catch (InvalidDataException) { throw new DocumentException($"This {noun} is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
        catch (XmlException) { throw new DocumentException($"This {noun} is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
        if (content.Parts.Sum(p => (long)p.Html.Length) + content.Styles.Values.Sum(s => (long)s.Length) > TextBudget)
            throw new DocumentException($"This {noun} holds more text than this viewer can show at once.");
        using (var file = new FileStream(Path.Combine(folder, Output), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(file, content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        notes.AddRange(SheetDrawings.Notes(budget));
        return new DocumentView { Kind = "web", Encoding = label, Store = Output, Text = content.Title, Notice = string.Join(" ", notes) };
    }

    // ---- Web page ----

    private static readonly Regex MetaCharset = new(@"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]{1,40})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The page's text: its byte order mark, else the charset its <meta> declares, else the text-file detection.
    public static string Page(byte[] bytes)
    {
        ReadOnlySpan<byte> s = bytes;
        if (s.StartsWith("%PDF-"u8) || s.StartsWith("PK\u0003\u0004"u8) || s.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 }) || s.StartsWith("MZ"u8) || ImageFiles.Identify(bytes) is not null)
            throw new DocumentException("The file contents do not match a web page. Open it with an application for its actual format.");
        return Decode(bytes, null);
    }

    private static string Decode(byte[] bytes, string? declared)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var (encoding, skip) = TextFiles.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 65536)).ToArray());
        if (skip == 0)
        {
            var meta = MetaCharset.Match(Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096)));
            string? name = declared ?? (meta.Success ? meta.Groups[1].Value : null);
            if (name is not null)
                try { encoding = Encoding.GetEncoding(name.Equals("utf-16", StringComparison.OrdinalIgnoreCase) ? "utf-8" : name); } catch (ArgumentException) { }
        }
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new DocumentException("This file contains binary data, so its contents are not a web page. Open it with an application for its actual format.");
        // Other control characters are dropped, as a browser would ignore them.
        return text.Any(c => c < 0x20 && c is not ('\n' or '\r' or '\t' or '\f')) ? new string(text.Where(c => c >= 0x20 || c is '\n' or '\r' or '\t' or '\f').ToArray()) : text;
    }

    // ---- Saved web archive (MIME, as Internet Explorer, Edge and Word save .mht) ----

    private static Content Archive(byte[] bytes, string folder, SheetDrawings.Budget budget)
    {
        string raw = Encoding.Latin1.GetString(bytes);
        var (headers, body) = SplitHeaders(raw);
        if (!headers.ContainsKey("content-type")) throw new DocumentException("This file is named as a web archive, but its contents are not one. Open it with an application for its actual format.");
        var content = new Content { Kind = "archive" };
        var parts = new List<(Dictionary<string, string> Headers, string Body)>();
        string type = headers["content-type"];
        if (Parameter(type, "boundary") is { Length: > 0 } boundary)
        {
            foreach (var piece in body.Split("--" + boundary).Skip(1))
            {
                if (piece.StartsWith("--")) break;
                parts.Add(SplitHeaders(piece.TrimStart('\r', '\n')));
                if (parts.Count > 20000) throw new InvalidDataException();
            }
        }
        else parts.Add((headers, body));
        Part? main = null;
        foreach (var (partHeaders, partBody) in parts)
        {
            string partType = partHeaders.GetValueOrDefault("content-type", "text/plain");
            string mime = partType.Split(';')[0].Trim().ToLowerInvariant();
            byte[] data = Transfer(partHeaders.GetValueOrDefault("content-transfer-encoding", ""), partBody);
            var keys = new List<string>();
            if (partHeaders.TryGetValue("content-location", out var location) && location.Length > 0) keys.Add(location.Trim());
            if (partHeaders.TryGetValue("content-id", out var id) && id.Trim().Trim('<', '>') is { Length: > 0 } cid) keys.Add("cid:" + cid);
            if (mime is "text/html" or "application/xhtml+xml" && main is null)
                main = new Part { Name = keys.FirstOrDefault(k => !k.StartsWith("cid:")) ?? "", Html = Decode(data, Parameter(partType, "charset")) };
            else if (mime == "text/css")
                foreach (var key in keys) content.Styles[key] = Decode(data, Parameter(partType, "charset"));
            else if (mime.StartsWith("image/") && keys.Count > 0 && SavePicture(data, folder, budget, content.Pictures.Values.Distinct().Count()) is { } name)
                foreach (var key in keys) content.Pictures[key] = name;
        }
        content.Parts.Add(main ?? throw new DocumentException("This web archive holds no web page. Open it with an application for its actual format."));
        return content;
    }

    private static (Dictionary<string, string> Headers, string Body) SplitHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is int crlf and >= 0 ? crlf : text.IndexOf("\n\n", StringComparison.Ordinal);
        if (end < 0) return (headers, "");
        string? name = null;
        foreach (var line in text[..end].Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && name is not null) { headers[name] += " " + line.Trim(); continue; }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            name = line[..colon].Trim();
            headers[name] = line[(colon + 1)..].Trim();
        }
        return (headers, text[(end + (text[end] == '\r' ? 4 : 2))..]);
    }

    private static string? Parameter(string header, string name) =>
        Regex.Match(header, $@"(?:^|;)\s*{name}\s*=\s*(?:""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } m
            ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;

    private static byte[] Transfer(string encoding, string body)
    {
        switch (encoding.Trim().ToLowerInvariant())
        {
            case "base64":
                var clean = new StringBuilder(body.Length);
                foreach (char c in body) if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=') clean.Append(c);
                try { return Convert.FromBase64String(clean.ToString()); } catch (FormatException) { throw new InvalidDataException(); }
            case "quoted-printable":
                var output = new MemoryStream(body.Length);
                for (int i = 0; i < body.Length; i++)
                {
                    char c = body[i];
                    if (c != '=') { output.WriteByte((byte)c); continue; }
                    if (i + 1 < body.Length && body[i + 1] == '\n') { i += 1; continue; }
                    if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n') { i += 2; continue; }
                    if (i + 2 < body.Length && Uri.IsHexDigit(body[i + 1]) && Uri.IsHexDigit(body[i + 2])) { output.WriteByte(Convert.ToByte(body.Substring(i + 1, 2), 16)); i += 2; continue; }
                    output.WriteByte((byte)c);
                }
                return output.ToArray();
            default: return Encoding.Latin1.GetBytes(body);
        }
    }

    // Saves a picture stored in the file to the work folder, if it is one the picture view shows. Returns its media name.
    private static string? SavePicture(byte[] bytes, string folder, SheetDrawings.Budget budget, int index)
    {
        if (!SheetDrawings.Fits(bytes.Length, budget)) return null;
        var picture = ImageFiles.Identify(bytes);
        if (picture is null || picture.Format is "SVG" or "HEIF" || (long)picture.Width * picture.Height > ImageFiles.PixelLimit || index > 9999) { budget.Unsupported++; return null; }
        budget.Bytes -= bytes.Length; budget.Pictures--;
        string name = $"media-0-{index}.{picture.ContentType.Split('/')[1].Replace("x-icon", "ico")}";
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
        return name;
    }

    // ---- EPUB book ----

    private const string ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private const string OpfNs = "http://www.idpf.org/2007/opf";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XmlReaderSettings XmlSettings = new()
    { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1024, MaxCharactersInDocument = 64L * 1024 * 1024, IgnoreComments = true, IgnoreProcessingInstructions = true };

    private static Content Book(byte[] bytes, string folder, SheetDrawings.Budget budget, List<string> notes)
    {
        if (!bytes.AsSpan().StartsWith("PK\u0003\u0004"u8)) throw new DocumentException("This file is named .epub, but its contents are not an EPUB book. Open it with an application for its actual format.");
        using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        ArchiveSafety.Validate(zip, maximumBytes: 1024L * 1024 * 1024, maximumEntries: 20000, maximumRatio: 200);
        if (zip.GetEntry("META-INF/container.xml") is not { } container)
            throw new DocumentException("This file is named .epub, but its contents are not an EPUB book. Open it with an application for its actual format.");
        // Books protected with DRM keep their pages encrypted; only fonts may be listed, obfuscated with the IDPF or
        // Adobe scheme (undone below, as reading systems do), not protected.
        var obfuscated = new Dictionary<string, string>();   // font path -> algorithm
        if (zip.GetEntry("META-INF/encryption.xml") is { } encryption)
        {
            using var reader = XmlReader.Create(encryption.Open(), XmlSettings);
            string algorithm = "";
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "EncryptionMethod") algorithm = reader.GetAttribute("Algorithm") ?? "";
                else if (reader.LocalName == "CipherReference")
                {
                    string uri = Uri.UnescapeDataString(reader.GetAttribute("URI") ?? "");
                    if (!Regex.IsMatch(uri, @"\.(otf|ttf|woff2?)$", RegexOptions.IgnoreCase) || algorithm is not (IdpfObfuscation or AdobeObfuscation))
                        throw new DocumentException("This book is protected with DRM (copy protection), so it can only be read in the app it was bought for.");
                    obfuscated[Resolve("", uri)] = algorithm;
                }
            }
        }
        string? opfPath = null;
        using (var reader = XmlReader.Create(container.Open(), XmlSettings))
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rootfile" && reader.GetAttribute("full-path") is { Length: > 0 } full) { opfPath = full; break; }
        if (opfPath is null || zip.GetEntry(opfPath) is not { } opf) throw new InvalidDataException();
        string root = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var manifest = new Dictionary<string, (string Href, string Type)>();
        var spine = new List<string>();
        var content = new Content { Kind = "book" };
        string? uniqueId = null;
        var identifiers = new Dictionary<string, string>();
        using (var reader = XmlReader.Create(opf.Open(), XmlSettings))
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "package") uniqueId = reader.GetAttribute("unique-identifier");
                else if (reader.LocalName == "identifier" && reader.NamespaceURI == DcNs) { string key = reader.GetAttribute("id") ?? ""; string value = reader.ReadElementContentAsString(); identifiers.TryAdd(key, value); }
                else if (reader.LocalName == "item" && reader.GetAttribute("id") is { } id && reader.GetAttribute("href") is { } href)
                    manifest[id] = (Resolve(root, Uri.UnescapeDataString(href)), reader.GetAttribute("media-type") ?? "");
                else if (reader.LocalName == "itemref" && reader.GetAttribute("idref") is { } idref) spine.Add(idref);
                else if (reader.LocalName == "title" && reader.NamespaceURI == DcNs && content.Title.Length == 0) content.Title = reader.ReadElementContentAsString().Trim();
            }
        int pictures = 0, fonts = 0;
        string identifier = uniqueId is not null && identifiers.TryGetValue(uniqueId, out var found) ? found : identifiers.Values.FirstOrDefault() ?? "";
        foreach (var (href, type) in manifest.Values)
        {
            if (zip.GetEntry(href) is not { } entry) continue;
            if (type.StartsWith("image/"))
            {
                if (SavePicture(Read(entry), folder, budget, pictures) is { } name) { content.Pictures[href] = name; pictures++; }
            }
            else if (type == "text/css") content.Styles[href] = Decode(Read(entry), null);
            else if ((type.Contains("font", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(href, @"\.(otf|ttf|woff2?)$", RegexOptions.IgnoreCase)) && fonts < 100 && entry.Length <= 16 * 1024 * 1024)
            {
                byte[] font = Read(entry);
                if (obfuscated.TryGetValue(href, out var scheme)) Deobfuscate(font, scheme, identifier);
                if (FontType(font) is { } extension) { string name = $"font-{fonts++}.{extension}"; File.WriteAllBytes(Path.Combine(folder, name), font); content.Fonts[href] = name; }
            }
        }
        foreach (var idref in spine)
        {
            if (!manifest.TryGetValue(idref, out var item) || item.Type is not ("application/xhtml+xml" or "text/html") || zip.GetEntry(item.Href) is not { } entry) continue;
            content.Parts.Add(new Part { Name = item.Href, Html = Decode(Read(entry), null) });
        }
        if (content.Parts.Count == 0) throw new DocumentException("This book has no pages to show. It may be damaged; try another copy of the file.");
        return content;
    }

    private const string IdpfObfuscation = "http://www.idpf.org/2008/embedding", AdobeObfuscation = "http://ns.adobe.com/pdf/enc#RC";

    // Font obfuscation (not protection): IDPF XORs the first 1,040 bytes with the SHA-1 of the book's identifier
    // (without white space); Adobe XORs the first 1,024 bytes with the 16 bytes of its UUID.
    private static void Deobfuscate(byte[] font, string scheme, string identifier)
    {
        byte[] key;
        int length;
        if (scheme == IdpfObfuscation)
        {
            key = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(new string(identifier.Where(c => c is not (' ' or '\t' or '\r' or '\n')).ToArray())));
            length = 1040;
        }
        else
        {
            string hex = new(identifier.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase).Where(Uri.IsHexDigit).ToArray());
            if (hex.Length < 32) return;
            key = Convert.FromHexString(hex[..32]);
            length = 1024;
        }
        for (int i = 0; i < Math.Min(length, font.Length); i++) font[i] ^= key[i % key.Length];
    }

    // The kind of font, from its first bytes; null for anything else.
    public static string? FontType(ReadOnlySpan<byte> b) =>
        b.Length < 12 ? null : b.StartsWith("wOFF"u8) ? "woff" : b.StartsWith("wOF2"u8) ? "woff2" : b.StartsWith("OTTO"u8) ? "otf"
        : b.StartsWith((ReadOnlySpan<byte>)[0, 1, 0, 0]) || b.StartsWith("true"u8) ? "ttf" : null;

    // Fonts the worker wrote for a book: "font-<n>.<type>", and their content type.
    public static readonly Regex FontName = new(@"^font-\d{1,3}\.(ttf|otf|woff|woff2)$", RegexOptions.CultureInvariant);
    public static string? FontContentType(string name) => FontName.Match(name) is { Success: true } m ? "font/" + m.Groups[1].Value : null;

    private static byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > 64L * 1024 * 1024) throw new DocumentException("A part of this book is larger than this viewer can open safely.");
        using var stream = entry.Open();
        var buffer = new byte[entry.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    // A path inside the book, relative to the folder of the part that names it; never above the book's root.
    public static string Resolve(string folder, string href)
    {
        var parts = new List<string>();
        foreach (var piece in (folder + href.Split('#')[0]).Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(piece);
        }
        return string.Join('/', parts);
    }
}
