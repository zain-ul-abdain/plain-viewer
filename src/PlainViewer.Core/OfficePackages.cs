using System.IO.Compression;
using System.Text;
using System.Xml;
namespace PlainViewer.Core;

// Prepares a Word or PowerPoint package for conversion. Runs in the worker, before LibreOffice sees the file:
// refuses encrypted, mislabelled and malformed packages, then writes a private copy with every reference to outside
// content removed (pictures, templates or objects stored elsewhere), content-fetching field codes blanked, and any
// macro project removed. Templates, shows and macro-enabled files are relabelled as ordinary documents in the copy,
// so the converter only ever sees a plain .docx or .pptx. Web and email hyperlinks are kept; the viewer asks before
// opening them.
public static class OfficePackages
{
    public const long SizeLimit = 256L * 1024 * 1024;
    private const long PartByteLimit = 512L * 1024 * 1024;
    private static readonly string[] FetchingFields = ["INCLUDEPICTURE", "INCLUDETEXT", "LINK", "DDE", "DDEAUTO", "IMPORT", "DATABASE"];
    public static readonly string[] WordExtensions = [".docx", ".docm", ".dotx", ".dotm"];
    public static readonly string[] SlideExtensions = [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"];
    private const string WordMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string SlidesMain = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    // Main-part types of the variants, relabelled in the private copy.
    private static readonly Dictionary<string, string> VariantTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.ms-word.document.macroEnabled.main+xml"] = WordMain,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"] = WordMain,
        ["application/vnd.ms-word.template.macroEnabledTemplate.main+xml"] = WordMain,
        ["application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml"] = SlidesMain,
        ["application/vnd.openxmlformats-officedocument.presentationml.template.main+xml"] = SlidesMain,
        ["application/vnd.ms-powerpoint.template.macroEnabled.main+xml"] = SlidesMain,
        ["application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml"] = SlidesMain,
        ["application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml"] = SlidesMain,
    };

    public static bool IsOfficeDocument(string path) => IsWord(path) || SlideExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsWord(string path) => WordExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // Macro projects, their signatures and their data. Never copied; relationships and type entries for them are dropped.
    public static bool IsMacroPart(string name)
    {
        string file = name[(name.LastIndexOf('/') + 1)..];
        return file.StartsWith("vbaProject", StringComparison.OrdinalIgnoreCase) || file.Equals("vbaData.xml", StringComparison.OrdinalIgnoreCase);
    }

    public static DocumentView Prepare(string path, string output)
    {
        TextFiles.ValidateLocalPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!IsOfficeDocument(path))
            throw new DocumentException($"{extension} files do not open in the Word and PowerPoint view.");
        bool word = IsWord(path);
        string kind = word ? "Word document" : "PowerPoint presentation";

        using var stream = LocalFiles.OpenRead(path);
        long length = stream.Length;
        var stamp = LocalFiles.Stamp(stream);
        if (length == 0) throw new DocumentException($"This {kind} is empty (0 bytes). It may not have finished downloading or copying. Get a complete copy and try again.");
        if (length > SizeLimit) throw new DocumentException($"This {kind} is larger than 256 MB, which is more than this viewer can open safely.");
        byte[] head = new byte[Math.Min(length, 65536)];
        stream.ReadExactly(head); stream.Position = 0;
        // Password protected: decrypted in memory with the password the user typed (OfficeEncryption); only the
        // cleaned copy for the converter is written, to the private work folder, as for any other document.
        Stream package = stream;
        if (head.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
        {
            if (head.AsSpan().IndexOf(Encoding.Unicode.GetBytes("EncryptionInfo")) < 0)
                throw new DocumentException($"This looks like an older Office file ({(word ? ".doc" : ".ppt")}) saved with a {extension} name. Rename it to end in {(word ? ".doc" : ".ppt")} to view it.");
            var encrypted = new byte[length]; stream.ReadExactly(encrypted); stream.Position = 0;
            try { package = new MemoryStream(OfficeEncryption.Decrypt(encrypted, kind), false); }
            catch (InvalidDataException) { throw new DocumentException($"This {kind} is damaged or incomplete, so it cannot be shown. Try another copy of the file."); }
            head = new byte[8]; package.ReadExactly(head); package.Position = 0;
        }
        if (!head.AsSpan().StartsWith("PK\u0003\u0004"u8))
            throw new DocumentException($"This file is named {extension}, but its contents are not a {kind}. Open it with an application for its actual format.");

        int removed = 0;
        bool macros = false;
        bool ownsOutput = false;
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            ArchiveSafety.Validate(zip, maximumBytes: 2L * 1024 * 1024 * 1024, maximumEntries: 10000, maximumRatio: 500);
            if (zip.GetEntry(word ? "word/document.xml" : "ppt/presentation.xml") is null)
                throw new DocumentException($"This file is named {extension}, but its contents are not a {kind}. Open it with an application for its actual format.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            // Only clean up files this invocation actually created. CreateNew can fail because
            // another file already exists, including when the caller passes the source path.
            using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsOutput = true;
            using var target = new ZipArchive(outputStream, ZipArchiveMode.Create);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (IsMacroPart(entry.FullName)) { macros = true; continue; }
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                using var input = new LimitedStream(entry.Open(), PartByteLimit);
                using var destination = copy.Open();
                if (entry.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) CopyXml(input, destination, Mode.ContentTypes);
                else if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) removed += CopyXml(input, destination, Mode.Relationships);
                else if (word && entry.FullName.StartsWith("word/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) removed += CopyXml(input, destination, Mode.WordFields);
                else if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) CopyXml(input, destination, Mode.Validate);
                else input.CopyTo(destination);
            }
        }
        catch (InvalidDataException) { if (ownsOutput) Discard(output); throw Damaged(kind); }
        catch (XmlException) { if (ownsOutput) Discard(output); throw Damaged(kind); }
        catch { if (ownsOutput) Discard(output); throw; }
        if (LocalFiles.Stamp(stream) != stamp)
        { Discard(output); throw new DocumentException(LocalFiles.ChangedMessage); }
        return new DocumentView
        {
            Kind = word ? "word" : "slides",
            Encoding = word ? "Word document" : "PowerPoint presentation",
            Notice = string.Join(" ", new[]
            {
                macros ? "This file contains macros. They were removed before display and never ran." : "",
                removed > 0 ? $"{removed} reference{(removed == 1 ? "" : "s")} to content stored outside this file {(removed == 1 ? "was" : "were")} removed before display, so linked pictures or templates are not shown." : ""
            }.Where(text => text.Length > 0))
        };
    }

    private static DocumentException Damaged(string kind) => new($"This {kind} is damaged or incomplete, so it cannot be shown. Try another copy of the file.");
    private static void Discard(string output) { try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { } }

    private enum Mode { Validate, Relationships, WordFields, ContentTypes }

    // Streams one XML part through a reader with DTDs prohibited and writes it back, dropping outside references and
    // links to macro parts (Relationships), blanking content-fetching field codes (WordFields), or relabelling variant
    // main parts and dropping macro part types (ContentTypes). Returns how many outside references were removed.
    private static int CopyXml(Stream input, Stream output, Mode mode)
    {
        int removed = 0;
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024, IgnoreComments = true, CloseInput = false });
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        bool inInstruction = false;
        bool advance = true;
        while (advance ? reader.Read() : !reader.EOF)
        {
            advance = true;
            switch (reader.NodeType)
            {
                case XmlNodeType.XmlDeclaration:
                    writer.WriteStartDocument(reader.GetAttribute("standalone") == "yes");
                    break;
                case XmlNodeType.Element:
                    if (mode == Mode.Relationships && reader.LocalName == "Relationship" && IsExternalRelationship(reader)
                        && !IsAllowedHyperlink(reader))
                    {
                        removed++;
                        // Skip leaves the reader on the following node. Do not read again and
                        // lose a neighbouring relationship or its closing parent element.
                        if (!reader.IsEmptyElement) { reader.Skip(); advance = false; }
                        continue;
                    }
                    if ((mode == Mode.Relationships && reader.LocalName == "Relationship" && IsMacroPart(reader.GetAttribute("Target") ?? ""))
                        || (mode == Mode.ContentTypes && reader.LocalName == "Override" && IsMacroPart(reader.GetAttribute("PartName") ?? ""))
                        || (mode == Mode.ContentTypes && reader.LocalName == "Default" && (reader.GetAttribute("ContentType") ?? "").Contains("vba", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!reader.IsEmptyElement) { reader.Skip(); advance = false; }
                        continue;
                    }
                    bool empty = reader.IsEmptyElement;
                    writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            string value = reader.Value;
                            if (mode == Mode.WordFields && reader.LocalName == "instr" && IsFetchingField(value)) { value = ""; removed++; }
                            if (mode == Mode.ContentTypes && reader.LocalName == "ContentType" && VariantTypes.TryGetValue(value, out var plain)) value = plain;
                            writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, value);
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (empty) writer.WriteEndElement();
                    else if (mode == Mode.WordFields && reader.LocalName == "instrText") inInstruction = true;
                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == "instrText") inInstruction = false;
                    writer.WriteFullEndElement();
                    break;
                case XmlNodeType.Text:
                    if (inInstruction && IsFetchingField(reader.Value)) { removed++; writer.WriteString(""); }
                    else writer.WriteString(reader.Value);
                    break;
                case XmlNodeType.CDATA: writer.WriteCData(reader.Value); break;
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace: writer.WriteWhitespace(reader.Value); break;
                case XmlNodeType.ProcessingInstruction: writer.WriteProcessingInstruction(reader.Name, reader.Value); break;
            }
        }
        return removed;
    }

    private static bool IsFetchingField(string instruction)
    {
        var first = instruction.TrimStart().Split([' ', '\t', '\\', '"'], 2)[0];
        return FetchingFields.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExternalRelationship(XmlReader reader)
    {
        string target = reader.GetAttribute("Target") ?? "";
        return string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("\\\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal)
            // A single leading slash addresses a part within the OPC package, not the host filesystem.
            || (!target.StartsWith("/", StringComparison.Ordinal) && Uri.TryCreate(target, UriKind.Absolute, out _));
    }

    private static bool IsAllowedHyperlink(XmlReader reader) =>
        (reader.GetAttribute("Type") ?? "").EndsWith("/hyperlink", StringComparison.Ordinal)
        && LinkPolicy.CanOpen(reader.GetAttribute("Target"));

    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            read += n;
            if (read > limit) throw new DocumentException("This file is damaged or too large to open safely. Try another copy of the file.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
