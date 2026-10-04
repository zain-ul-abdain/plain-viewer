using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
namespace PlainViewer.Core;

// Formats shown through the bundled LibreOffice (0.3.0): OpenDocument (.odt .ods .odp), RTF, the older binary Office
// formats (.doc .xls .ppt) and TIFF pictures. Runs in the worker, before LibreOffice sees the file: identifies the file
// by its content, refuses password-protected, mislabelled and damaged files with a clear message, and writes a private
// copy with every reference to content stored elsewhere made harmless:
// - OpenDocument: attributes pointing outside the file are emptied (web and email hyperlinks are kept).
// - RTF: fetching fields, template references, linked objects and linked shape pictures are removed.
// - .doc/.xls/.ppt: content-fetching field codes are blanked, and web, file and network-share addresses in the file's
//   non-text parts are overwritten with spaces in place (lengths never change, so the structure stays valid).
// Tested with a request listener: LibreOffice fetched a linked picture from an unprepared .doc (see TEST-RESULTS.md).
public static partial class ConvertedDocuments
{
    public const long SizeLimit = 256L * 1024 * 1024;
    public static readonly string[] WordExtensions = [".odt", ".rtf", ".doc", ".dot", ".ott"];
    public static readonly string[] SlideExtensions = [".odp", ".ppt"];
    public static readonly string[] PictureExtensions = [".tif", ".tiff"];
    private static readonly string[] FetchingFields = ["INCLUDEPICTURE", "INCLUDETEXT", "LINK", "DDE", "DDEAUTO", "IMPORT", "DATABASE"];

    public static bool Handles(string path) => KindOf(path) is not null;

    // "word" (continuous pages), "slides" or "pages" (TIFF). Spreadsheets (.xls, .ods) are read directly by
    // LegacySpreadsheets instead, because LibreOffice recalculated their formulas.
    public static string? KindOf(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return WordExtensions.Contains(extension) ? "word" : SlideExtensions.Contains(extension) ? "slides"
            : PictureExtensions.Contains(extension) ? "pages" : null;
    }

    private enum Format { Odt, Ods, Odp, Rtf, Doc, Xls, Ppt, Tiff }

    private static string Label(Format format) => format switch
    {
        Format.Odt => "OpenDocument text", Format.Ods => "OpenDocument spreadsheet", Format.Odp => "OpenDocument presentation",
        Format.Rtf => "Rich Text document", Format.Doc => "Word 97–2003 document", Format.Xls => "Excel 97–2003 workbook",
        Format.Ppt => "PowerPoint 97–2003 presentation", _ => "TIFF picture"
    };

    private static string Noun(string kind) => kind switch { "word" => "document", "slides" => "presentation", "sheet" => "spreadsheet", _ => "picture" };

    // The extension LibreOffice should see for the private copy (the content decides, for example an RTF named .doc).
    public static string CopyExtension(DocumentView prepared) => prepared.Encoding switch
    {
        "OpenDocument text" => ".odt", "OpenDocument spreadsheet" => ".ods", "OpenDocument presentation" => ".odp",
        "Rich Text document" => ".rtf", "Word 97–2003 document" => ".doc", "Excel 97–2003 workbook" => ".xls",
        "PowerPoint 97–2003 presentation" => ".ppt", _ => ".tif"
    };

    public static DocumentView Prepare(string path, string output)
    {
        TextFiles.ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        string kind = KindOf(path) ?? throw new DocumentException($"{extension} files do not open in this view.");
        string noun = Noun(kind);
        byte[] bytes;
        using (var stream = LocalFiles.OpenRead(path))
        {
            long length = stream.Length;
            var stamp = LocalFiles.Stamp(stream);
            if (length == 0) throw new DocumentException($"This {noun} is empty (0 bytes). It may not have finished downloading or copying. Get a complete copy and try again.");
            if (length > SizeLimit) throw new DocumentException($"This {noun} is larger than 256 MB, which is more than this viewer can open safely.");
            bytes = new byte[length];
            stream.ReadExactly(bytes);
            LocalFiles.ThrowIfChanged(stream, stamp);
        }

        Format format;
        try { format = Identify(bytes, extension, kind, noun); }
        catch (InvalidDataException) { throw new DocumentException($"This {noun} is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
        int removed = 0; bool macros = false;
        var view = new DocumentView { Kind = kind == "pages" ? "word" : kind, Encoding = Label(format) };
        string label = Label(format);
        try
        {
            byte[] copy = format switch
            {
                Format.Odt or Format.Ods or Format.Odp => OpenDocument(Decrypted(bytes, label), label, ref removed, ref macros),
                Format.Rtf => Rtf(bytes, ref removed, ref macros),
                Format.Doc => Word97(bytes, label, ref removed, ref macros),
                Format.Ppt => PowerPoint97(bytes, label, ref removed, ref macros),
                _ => bytes
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            target.Write(copy);
        }
        catch (InvalidDataException) { throw Damaged(label); }
        catch (XmlException) { throw Damaged(label); }
        view.Notice = string.Join(" ", new[]
        {
            macros ? "This file contains macros. They never run in this viewer." : "",
            removed > 0 ? $"{removed} reference{(removed == 1 ? "" : "s")} to content stored outside this file {(removed == 1 ? "was" : "were")} removed before display, so linked pictures, templates or data are not shown." : ""
        }.Where(text => text.Length > 0));
        return view;
    }

    private static DocumentException Damaged(string label) => new($"This {label} is damaged or incomplete, so it cannot be shown. Try another copy of the file.");

    // The file's real format, from its bytes; refusals for password-protected, mislabelled and unsupported files.
    private static Format Identify(byte[] bytes, string extension, string kind, string noun)
    {
        ReadOnlySpan<byte> s = bytes;
        string Named() => $"This file is named {extension}, but its contents are not a {noun} this view can show. Open it with an application for its actual format.";
        if (CompoundFile.IsCompoundFile(s))
        {
            var file = new CompoundFile(bytes, noun);
            // A password-protected newer Office file (an encrypted .docx/.pptx package) with an older file's name.
            if (file.Has("EncryptionInfo") || file.Has("EncryptedPackage"))
                throw new DocumentException($"This is a password-protected newer Office file (such as .docx or .pptx) saved with a {extension} name. Rename it with the right ending to view it.");
            var format = file.Has("WordDocument") ? Format.Doc : file.Has("Workbook") || file.Has("Book") ? Format.Xls : file.Has("PowerPoint Document") ? Format.Ppt : (Format?)null;
            if (format is null || KindOf("x" + (format == Format.Doc ? ".doc" : format == Format.Xls ? ".xls" : ".ppt")) != kind) throw new DocumentException(Named());
            return format.Value;
        }
        if (s.StartsWith("{\\rtf"u8)) return kind == "word" ? Format.Rtf : throw new DocumentException(Named());
        if (s.StartsWith("II*\0"u8) || s.StartsWith("MM\0*"u8) || s.StartsWith("II+\0"u8) || s.StartsWith("MM\0+"u8))
            return kind == "pages" ? Format.Tiff : throw new DocumentException(Named());
        if (s.StartsWith("PK\u0003\u0004"u8))
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
            if (zip.GetEntry("[Content_Types].xml") is not null)
                throw new DocumentException($"This is a newer Office file (such as .docx, .xlsx or .pptx) saved with a {extension} name. Rename it with the right ending to view it.");
            string mime = "";
            if (zip.GetEntry("mimetype") is { Length: < 200 } entry) using (var reader = new StreamReader(entry.Open())) mime = reader.ReadToEnd().Trim();
            // Templates (.ott, 0.8.0) are shown as the document they hold; Prepare labels the copy as a document.
            var format = mime.Replace("-template", "") switch
            {
                "application/vnd.oasis.opendocument.text" => Format.Odt,
                "application/vnd.oasis.opendocument.spreadsheet" => Format.Ods,
                "application/vnd.oasis.opendocument.presentation" => Format.Odp,
                _ => (Format?)null
            };
            if (format is null || KindOf("x" + (format == Format.Odt ? ".odt" : format == Format.Ods ? ".ods" : ".odp")) != kind) throw new DocumentException(Named());
            return format.Value;
        }
        throw new DocumentException(Named());
    }

    // ---- OpenDocument ----

    // A password-protected OpenDocument file, decrypted in memory with the password the user typed (OpenDocumentEncryption).
    private static byte[] Decrypted(byte[] bytes, string label)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        ArchiveSafety.Validate(zip, maximumBytes: 2L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
        return OpenDocumentEncryption.IsEncrypted(zip) ? OpenDocumentEncryption.Decrypt(zip, label) : bytes;
    }

    // A template's media type ("…opendocument.text-template"): the copy is labelled as the document type instead.
    private static readonly Regex TemplateType = new(@"(application/vnd\.oasis\.opendocument\.[a-z]+)-template", RegexOptions.CultureInvariant);

    private static byte[] OpenDocument(byte[] bytes, string label, ref int removed, ref bool macros)
    {
        using var source = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        ArchiveSafety.Validate(source, maximumBytes: 2L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
        if (source.GetEntry("content.xml") is null) throw Damaged(label);
        var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (entry.FullName.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase)) macros = true;
                // The mimetype entry stays first and uncompressed, as the format requires.
                var copy = target.CreateEntry(entry.FullName, entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                using var input = entry.Open();
                using var destination = copy.Open();
                if (entry.FullName.Equals("META-INF/manifest.xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var buffer = new MemoryStream(); input.CopyTo(buffer);
                    string manifest = Encoding.UTF8.GetString(buffer.ToArray());
                    if (manifest.Contains("encryption-data", StringComparison.Ordinal))
                        throw new DocumentException($"This {label} is protected with a password. Password-protected files cannot be opened in this version. Remove the password in the application that made it, or ask the sender for an unprotected copy.");
                    destination.Write(Encoding.UTF8.GetBytes(TemplateType.Replace(manifest, "$1")));
                }
                else if (entry.FullName == "mimetype")
                {
                    using var reader = new StreamReader(input);
                    destination.Write(Encoding.ASCII.GetBytes(TemplateType.Replace(reader.ReadToEnd(), "$1")));
                }
                else if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) removed += FilterXml(input, destination);
                else input.CopyTo(destination);
            }
        }
        return output.ToArray();
    }

    // Copies an XML part, emptying every attribute that points outside the file: external addresses (web, file,
    // network share) anywhere, and scripts and DDE. Hyperlinks (text:a, draw:a) to web and email addresses are kept.
    private static int FilterXml(Stream input, Stream output)
    {
        int removed = 0;
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 1024, IgnoreComments = true, CloseInput = false });
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.XmlDeclaration: writer.WriteStartDocument(); break;
                case XmlNodeType.Element:
                    bool empty = reader.IsEmptyElement, link = reader.LocalName == "a";
                    writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            string value = reader.Value;
                            if (reader.Prefix == "xmlns" || reader.LocalName == "xmlns") { writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, value); continue; }
                            bool hyperlink = link && reader.LocalName == "href" && LinkPolicy.CanOpen(value);
                            // A formula referring to another file ('file:///...'#Sheet.A1) loses its formula; its saved value stays.
                            bool externalFormula = reader.LocalName == "formula" && (value.Contains("file:", StringComparison.OrdinalIgnoreCase) || value.Contains("://", StringComparison.Ordinal) || value.Contains(@"\\", StringComparison.Ordinal));
                            if (!hyperlink && (externalFormula || Outside(value) || reader.LocalName is "dde-application" or "dde-topic" or "dde-item" || value.StartsWith("vnd.sun.star.script:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("macro:", StringComparison.OrdinalIgnoreCase)))
                            { if (value.Length > 0) removed++; value = ""; }
                            writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, value);
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (empty) writer.WriteEndElement();
                    break;
                case XmlNodeType.EndElement: writer.WriteFullEndElement(); break;
                case XmlNodeType.Text: writer.WriteString(reader.Value); break;
                case XmlNodeType.CDATA: writer.WriteCData(reader.Value); break;
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace: writer.WriteWhitespace(reader.Value); break;
            }
        }
        return removed;
    }

    // An address outside the file: any absolute URI (other than the package's own "vnd.sun.star.*"), a network path
    // or a file path with a drive letter.
    private static bool Outside(string value)
    {
        string v = value.Trim();
        if (v.Length < 3) return false;
        if (v.StartsWith(@"\\", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal)) return true;
        if (char.IsAsciiLetter(v[0]) && v[1] == ':' && v[2] is '\\' or '/') return true;
        return OutsideAddress().IsMatch(v);
    }
    // Schemes that reach other computers or the file system (formulas such as "of:=..." are not addresses).
    [GeneratedRegex(@"^(?:https?|s?ftps?|file|smb|nfs|webdavs?|davs?|mhtml|ldaps?|jar|vnd\.sun\.star\.webdav):", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OutsideAddress();

    // ---- RTF ----

    // Removes: {\*\template ...}; fetching field instructions; linked objects (\objlink, \objautlink) and linked shape
    // pictures (their {\sv ...} values with an outside address). Text and embedded content stay.
    private static byte[] Rtf(byte[] bytes, ref int removed, ref bool macros)
    {
        var text = Encoding.Latin1.GetString(bytes).ToCharArray();
        var groups = new Stack<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                // \binN is followed by N raw bytes, which may contain braces: skip them.
                if (i + 4 < text.Length && text[i + 1] == 'b' && text[i + 2] == 'i' && text[i + 3] == 'n' && char.IsAsciiDigit(text[i + 4]))
                {
                    int j = i + 4; long n = 0;
                    while (j < text.Length && char.IsAsciiDigit(text[j]) && n < int.MaxValue) n = n * 10 + (text[j++] - '0');
                    if (j < text.Length && text[j] == ' ') j++;
                    i = (int)Math.Min(text.Length - 1, j + n - 1);
                }
                else i++;                                             // an escaped character, or the start of a control word
                continue;
            }
            if (c == '{') { groups.Push(i); continue; }
            if (c != '}' || groups.Count == 0) continue;
            int start = groups.Pop();
            string group = new(text, start, i - start + 1);
            // How much of the group's start to keep ("{\*\template", "{\sv"), so the document keeps its structure.
            int keep = 0;
            if (group.StartsWith(@"{\*\template", StringComparison.Ordinal)) keep = 12;
            else if (group.StartsWith(@"{\*\fldinst", StringComparison.Ordinal))
            {
                string instruction = Regex.Replace(group[11..^1], @"\\[a-zA-Z]+-?\d* ?|[{}]", " ").TrimStart();
                if (FetchingFields.Any(f => instruction.StartsWith(f, StringComparison.OrdinalIgnoreCase) && (instruction.Length == f.Length || !char.IsAsciiLetter(instruction[f.Length])))) keep = 11;
            }
            else if (group.StartsWith(@"{\object", StringComparison.Ordinal) && Regex.IsMatch(group, @"\\obj(aut)?link(?![a-z])")) keep = 1;
            else if (group.StartsWith(@"{\sv ", StringComparison.Ordinal) && Outside(group[5..^1].Trim())) keep = 4;
            if (keep == 0) continue;
            for (int k = start + keep; k < i; k++) text[k] = ' ';
            removed++;
        }
        return Encoding.Latin1.GetBytes(text);
    }

    // ---- Word, Excel and PowerPoint 97-2003 ----

    // Overwrites web, file and network-share addresses with spaces, in single-byte and UTF-16 text. Returns how many.
    public static int BlankAddresses(byte[] bytes, int start = 0, int length = -1)
    {
        if (length < 0) length = bytes.Length - start;
        int count = 0;
        var latin = Encoding.Latin1.GetString(bytes, start, length);
        foreach (Match m in AddressPattern().Matches(latin)) { for (int k = 0; k < m.Length; k++) bytes[start + m.Index + k] = 0x20; count++; }
        for (int shift = 0; shift < 2; shift++)
        {
            int usable = (length - shift) / 2 * 2;
            if (usable <= 0) continue;
            var wide = Encoding.Unicode.GetString(bytes, start + shift, usable);
            foreach (Match m in AddressPattern().Matches(wide))
            {
                for (int k = 0; k < m.Length; k++) { bytes[start + shift + (m.Index + k) * 2] = 0x20; bytes[start + shift + (m.Index + k) * 2 + 1] = 0; }
                count++;
            }
        }
        return count;
    }
    // A scheme with "://" (or "file:"), or "\\host\" where host is a real host name (so that random bytes in pictures
    // are very unlikely to match), then the rest of the address up to a control character or quote.
    [GeneratedRegex(@"(?i)(?:(?<![a-z])(?:https?|s?ftps?|smb|nfs|mhtml|webdavs?|davs?|ldaps?)://|(?<![a-z])file:[/\\]|\\\\[A-Za-z0-9._\-@$]{2,253}\\)[^\x00-\x1f""'<>]{0,2048}", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    private static byte[] Word97(byte[] bytes, string label, ref int removed, ref bool macros)
    {
        var file = new CompoundFile(bytes, label);
        var main = file.Find("WordDocument") ?? throw Damaged(label);
        byte[] word = file.Read(main, label);
        if (word.Length < 0x200 || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC) throw Damaged(label);
        if (BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2)) < 0x00C1)
            throw new DocumentException("This is a Word 6.0 or Word 95 document, which is older than this viewer supports. Save it in a newer format to view it.");
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        macros = file.Has("Macros");
        var table = file.Find((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw Damaged(label);
        byte[] tableBytes = file.Read(table, label);
        if ((flags & 0x0100) != 0)
        {
            // Password protected ([MS-DOC] 2.2.6): the encryption header starts the table stream (lKey bytes); the main
            // stream after its first 68 bytes, the rest of the table stream and the Data stream are encrypted in 512-byte
            // blocks. Decrypted in the private copy with the password the user typed, then marked as not encrypted.
            if ((flags & 0x8000) != 0) throw LegacyEncryption.Unsupported(label);    // XOR obfuscation
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(0x0E));
            if (headerSize <= 0 || headerSize > tableBytes.Length) throw Damaged(label);
            var key = LegacyEncryption.Open(tableBytes.AsSpan(0, headerSize), label);
            key.Decrypt(word, 512, 68, word.Length);
            key.Decrypt(tableBytes, 512, headerSize, tableBytes.Length);
            if (file.Find("Data") is { } dataStream) { var dataBytes = file.Read(dataStream, label); key.Decrypt(dataBytes, 512, 0, dataBytes.Length); file.Write(dataStream, dataBytes, label); }
            flags = (ushort)(flags & ~0x8100);
            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x0A), flags);
            BinaryPrimitives.WriteInt32LittleEndian(word.AsSpan(0x0E), 0);
            file.Write(main, word, label);
            file.Write(table, tableBytes, label);
        }

        // Field instructions are in the main text: find the piece table (Clx) through the FIB.
        int pos = 32;
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos)); pos += 2 + csw * 2;
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos)); pos += 2 + cslw * 4;
        int pairs = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos)); pos += 2;
        if (pairs <= 33 || pos + 34 * 8 > word.Length) throw Damaged(label);
        int fcClx = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(pos + 33 * 8));
        int lcbClx = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(pos + 33 * 8 + 4));
        if (fcClx < 0 || lcbClx <= 0 || (long)fcClx + lcbClx > tableBytes.Length) throw Damaged(label);
        int c = fcClx, end = fcClx + lcbClx;
        while (c < end && tableBytes[c] == 0x01) c += 3 + BinaryPrimitives.ReadInt16LittleEndian(tableBytes.AsSpan(c + 1));
        if (c + 5 > end || tableBytes[c] != 0x02) throw Damaged(label);
        int lcb = BinaryPrimitives.ReadInt32LittleEndian(tableBytes.AsSpan(c + 1));
        int plc = c + 5, pieces = (lcb - 4) / 12;
        if (pieces <= 0 || plc + lcb > end) throw Damaged(label);

        // The main text as (byte offset in the WordDocument stream, byte width) per character.
        var chars = new List<(int Offset, int Width, char Value)>();
        for (int p = 0; p < pieces; p++)
        {
            int cpStart = BinaryPrimitives.ReadInt32LittleEndian(tableBytes.AsSpan(plc + p * 4));
            int cpEnd = BinaryPrimitives.ReadInt32LittleEndian(tableBytes.AsSpan(plc + (p + 1) * 4));
            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(plc + (pieces + 1) * 4 + p * 8 + 2));
            bool compressed = (fc & 0x40000000) != 0;
            long offset = compressed ? (fc & 0x3FFFFFFF) / 2 : fc & 0x3FFFFFFF;
            int count = cpEnd - cpStart, width = compressed ? 1 : 2;
            if (count < 0 || offset + (long)count * width > word.Length || chars.Count + count > word.Length) throw Damaged(label);
            for (int k = 0; k < count; k++)
            {
                int at = (int)offset + k * width;
                chars.Add((at, width, compressed ? (char)word[at] : (char)BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(at))));
            }
        }
        // Field begin 0x13, separator 0x14, end 0x15; the instruction runs from begin to separator (or end).
        var open = new Stack<List<int>>();
        for (int i = 0; i < chars.Count; i++)
        {
            char v = chars[i].Value;
            if (v == '\u0013') { open.Push([]); continue; }
            if (open.Count == 0) continue;
            if (v is '\u0014' or '\u0015')
            {
                // A null entry stands for a field's result part (after its separator), which is not an instruction.
                if (open.Pop() is { } instruction)
                {
                    string text = new(instruction.Select(k => chars[k].Value).ToArray());
                    string first = text.TrimStart().Split([' ', '\t', '\\', '"'], 2)[0];
                    if (FetchingFields.Contains(first, StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (int k in instruction) { word[chars[k].Offset] = 0x20; if (chars[k].Width == 2) word[chars[k].Offset + 1] = 0; }
                        removed++;
                    }
                    if (v == '\u0014') open.Push(null!);
                }
                continue;
            }
            if (open.Peek() is { } current) current.Add(i);
        }
        file.Write(main, word, label);
        BlankOtherStreams(file, bytes, label, ["WordDocument"], ref removed);
        return bytes;
    }

    private static byte[] PowerPoint97(byte[] bytes, string label, ref int removed, ref bool macros)
    {
        var file = new CompoundFile(bytes, label);
        var main = file.Find("PowerPoint Document") ?? throw Damaged(label);
        byte[] records = file.Read(main, label);
        // Password protected: decrypted in the private copy with the password the user typed (LegacyEncryption).
        bool decrypted = false;
        if (file.Find("Current User") is { } userEntry && file.Read(userEntry, label) is { Length: >= 20 } user &&
            BinaryPrimitives.ReadUInt32LittleEndian(user.AsSpan(12)) == LegacyEncryption.EncryptedToken)
        {
            LegacyEncryption.DecryptPresentation(records, user, label);
            file.Write(userEntry, user, label);
            decrypted = true;
        }
        int count = 0; bool encrypted = false, vba = false;
        void Walk(int from, int to, int depth)
        {
            if (depth > 32) throw Damaged(label);
            for (int at = from; at + 8 <= to;)
            {
                int version = records[at] & 0x0F;
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(records.AsSpan(at + 2));
                long length = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(at + 4));
                if (at + 8 + length > to) throw Damaged(label);
                if (type == 0x2F14) encrypted = true;                  // CryptSession10Container
                if (type == 0x03FF) vba = true;                        // VBAInfoContainer
                if (version == 0x0F) Walk(at + 8, at + 8 + (int)length, depth + 1);
                // Every atom except the slides' own text: linked pictures, sounds, movies and objects keep their
                // addresses in other atoms (and OfficeArt properties), which are blanked.
                else if (type is not (0x0FA0 or 0x0FA8)) count += BlankAddresses(records, at + 8, (int)length);
                at += 8 + (int)length;
            }
        }
        Walk(0, records.Length, 0);
        // An encryption record without the Current User stream saying so: not a file this viewer can decrypt.
        if (encrypted && !decrypted) throw LegacyEncryption.Unsupported(label);
        macros = vba || file.Has("_VBA_PROJECT");
        removed += count;
        file.Write(main, records, label);
        BlankOtherStreams(file, bytes, label, ["PowerPoint Document", "Pictures"], ref removed);
        return bytes;
    }

    // Every other stream (tables, data, embedded and linked objects, summaries) holds no visible text of the document,
    // so any address in it is blanked.
    private static void BlankOtherStreams(CompoundFile file, byte[] bytes, string label, string[] skip, ref int removed)
    {
        foreach (var entry in file.Entries.Where(e => e.Type == 2 && e.Size > 0 && !skip.Contains(e.Name)))
        {
            byte[] stream = file.Read(entry, label);
            int count = BlankAddresses(stream);
            if (count == 0) continue;
            file.Write(entry, stream, label);
            removed += count;
        }
    }
}
