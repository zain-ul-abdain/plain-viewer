using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
namespace PlainViewer.Core;

// Password-protected OpenDocument files (.odt .ods .odp and templates). Their manifest (META-INF/manifest.xml) carries
// encryption-data for each encrypted part:
// - current LibreOffice (ODF 1.4 "wholesome" encryption): one part, encrypted-package, holding the whole inner
//   package; key from Argon2id, AES-256-GCM (its tag checks the password);
// - ODF 1.2/1.3 (older LibreOffice, Microsoft Office, other producers): each part encrypted on its own; key from
//   PBKDF2-HMAC-SHA1, AES-256-CBC, and a SHA-256 or SHA-1 checksum of the first kilobyte checks the password.
// Both start from a SHA-256 (or SHA-1) hash of the password. Parts are compressed before encryption. Blowfish, used by
// OpenOffice.org before 2008, is not supported. Returns the package with every part decrypted and the manifest
// without its encryption data, so the usual readers take it from there. The password is never stored.
public static class OpenDocumentEncryption
{
    private const string ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public static bool IsEncrypted(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/manifest.xml") is not { } manifest || manifest.Length > 16 * 1024 * 1024) return false;
        using var reader = new StreamReader(manifest.Open());
        return reader.ReadToEnd().Contains("encryption-data", StringComparison.Ordinal);
    }

    private sealed record Part(string Path, string Algorithm, byte[] Iv, string Derivation, byte[] Salt, int Iterations, int Memory, int Lanes,
        int KeySize, string StartHash, string Checksum, byte[]? ChecksumValue, long Size);

    public static byte[] Decrypt(ZipArchive zip, string kind)
    {
        if (OfficeEncryption.Password is not { Length: > 0 } password) throw new PasswordException(OfficeEncryption.Required(kind), false);
        var manifestEntry = zip.GetEntry("META-INF/manifest.xml")!;
        var document = new XmlDocument { XmlResolver = null };
        using (var stream = manifestEntry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            document.Load(reader);
        var parts = new Dictionary<string, Part>();
        foreach (XmlElement entry in document.GetElementsByTagName("file-entry", ManifestNs))
        {
            if (entry.GetElementsByTagName("encryption-data", ManifestNs).Cast<XmlElement>().FirstOrDefault() is not { } data) continue;
            var algorithm = data.GetElementsByTagName("algorithm", ManifestNs).Cast<XmlElement>().FirstOrDefault();
            var derivation = data.GetElementsByTagName("key-derivation", ManifestNs).Cast<XmlElement>().FirstOrDefault();
            var start = data.GetElementsByTagName("start-key-generation", ManifestNs).Cast<XmlElement>().FirstOrDefault();
            if (algorithm is null || derivation is null) throw Damaged(kind);
            string Attr(XmlElement e, string name) => e.GetAttribute(name, ManifestNs) is { Length: > 0 } value ? value
                : e.Attributes.Cast<XmlAttribute>().FirstOrDefault(a => a.LocalName == name.Split(':')[^1])?.Value ?? "";
            int Number(XmlElement e, string name, int fallback) => int.TryParse(Attr(e, name), out int n) ? n : fallback;
            byte[] Base64(string value) { try { return Convert.FromBase64String(value); } catch (FormatException) { throw Damaged(kind); } }
            string checksum = Attr(data, "checksum-type");
            parts[Attr(entry, "full-path")] = new Part(Attr(entry, "full-path"), Attr(algorithm, "algorithm-name"), Base64(Attr(algorithm, "initialisation-vector")),
                Attr(derivation, "key-derivation-name"), Base64(Attr(derivation, "salt")), Number(derivation, "iteration-count", 0),
                Number(derivation, "argon2-memory", 0), Number(derivation, "argon2-lanes", 0), Number(derivation, "key-size", 16),
                start is null ? "SHA1" : Attr(start, "start-key-generation-name"), checksum, checksum.Length > 0 ? Base64(Attr(data, "checksum")) : null,
                long.TryParse(Attr(entry, "size"), out long size) ? size : -1);
            if (Attr(derivation, "key-derivation-name").EndsWith("argon2id", StringComparison.Ordinal))
                parts[Attr(entry, "full-path")] = parts[Attr(entry, "full-path")] with { Iterations = Number(derivation, "argon2-iterations", 0) };
        }
        if (parts.Count == 0) throw Damaged(kind);

        var keys = new Dictionary<string, byte[]>();
        byte[] KeyFor(Part part)
        {
            string id = $"{part.Derivation}|{Convert.ToBase64String(part.Salt)}|{part.Iterations}|{part.Memory}|{part.Lanes}|{part.KeySize}|{part.StartHash}";
            if (keys.TryGetValue(id, out var cached)) return cached;
            byte[] utf8 = Encoding.UTF8.GetBytes(password);
            byte[] startKey = part.StartHash.EndsWith("sha256", StringComparison.OrdinalIgnoreCase) ? SHA256.HashData(utf8) : SHA1.HashData(utf8);
            byte[] key;
            if (part.Derivation.EndsWith("argon2id", StringComparison.Ordinal))
            {
                if (part.Iterations is < 1 or > 16 || part.Memory is < 8 or > 1024 * 1024 || part.Lanes is < 1 or > 64 || part.KeySize != 32) throw Damaged(kind);
                key = Argon2.Hash(startKey, part.Salt, part.Iterations, part.Memory, part.Lanes, 32);
            }
            else if (part.Derivation.EndsWith("PBKDF2", StringComparison.OrdinalIgnoreCase))
            {
                if (part.Iterations is < 1 or > 10_000_000 || part.KeySize is not (16 or 24 or 32)) throw Damaged(kind);
                key = Rfc2898DeriveBytes.Pbkdf2(startKey, part.Salt, part.Iterations, HashAlgorithmName.SHA1, part.KeySize);
            }
            else throw Unsupported(kind);
            return keys[id] = key;
        }

        byte[] Plain(Part part, byte[] encrypted)
        {
            byte[] key = KeyFor(part);
            byte[] decrypted;
            if (part.Algorithm.EndsWith("aes256-gcm", StringComparison.Ordinal))
            {
                // W3C XML Encryption 1.1 layout, as LibreOffice writes it: the 12-byte IV, the ciphertext, the 16-byte tag.
                if (encrypted.Length < 12 + 16) throw Damaged(kind);
                decrypted = new byte[encrypted.Length - 12 - 16];
                try
                {
                    using var gcm = new AesGcm(key, 16);
                    gcm.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(12, decrypted.Length), encrypted.AsSpan(12 + decrypted.Length), decrypted);
                }
                catch (AuthenticationTagMismatchException) { throw new PasswordException(OfficeEncryption.Incorrect(kind), true); }
            }
            else if (part.Algorithm.EndsWith("aes256-cbc", StringComparison.Ordinal))
            {
                if (part.Iv.Length != 16 || encrypted.Length % 16 != 0) throw Damaged(kind);
                using var aes = Aes.Create(); aes.Key = key;
                decrypted = aes.DecryptCbc(encrypted, part.Iv, PaddingMode.None);
                int pad = decrypted.Length > 0 ? decrypted[^1] : 0;                   // W3C padding: the last byte counts it
                if (pad is >= 1 and <= 16 && pad <= decrypted.Length) decrypted = decrypted.AsSpan(0, decrypted.Length - pad).ToArray();
                // The checksum covers the first kilobyte of the part without its padding.
                if (part.ChecksumValue is { } expected)
                {
                    var first = decrypted.AsSpan(0, Math.Min(1024, decrypted.Length));
                    byte[] actual = part.Checksum.Contains("sha256", StringComparison.OrdinalIgnoreCase) ? SHA256.HashData(first) : SHA1.HashData(first);
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new PasswordException(OfficeEncryption.Incorrect(kind), true);
                }
            }
            else throw Unsupported(kind);
            return Inflate(decrypted, part.Size, kind);
        }

        // Current LibreOffice: the whole inner package in one part.
        if (parts.TryGetValue("encrypted-package", out var whole) && zip.GetEntry("encrypted-package") is { } package)
            return Plain(whole, Read(package, kind));

        // ODF 1.2/1.3: each part on its own; a new package with the parts decrypted and the manifest without encryption data.
        var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                byte[] content = Read(entry, kind);
                if (parts.TryGetValue(entry.FullName, out var part)) content = Plain(part, content);
                else if (entry.FullName == "META-INF/manifest.xml")
                {
                    foreach (var data in document.GetElementsByTagName("encryption-data", ManifestNs).Cast<XmlElement>().ToList()) data.ParentNode!.RemoveChild(data);
                    using var text = new MemoryStream(); document.Save(text); content = text.ToArray();
                }
                using var stream = target.CreateEntry(entry.FullName, entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static byte[] Read(ZipArchiveEntry entry, string kind)
    {
        if (entry.Length > 512L * 1024 * 1024) throw Damaged(kind);
        using var stream = entry.Open();
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Parts are deflated (without a header) before encryption; some producers store small parts uncompressed.
    private static byte[] Inflate(byte[] data, long size, string kind)
    {
        try
        {
            using var inflater = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[81920];
            for (int n; (n = inflater.Read(buffer)) > 0;)
            {
                output.Write(buffer, 0, n);
                if (output.Length > 2L * 1024 * 1024 * 1024 || (size > 0 && output.Length > size)) throw Damaged(kind);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return data; }
    }

    private static DocumentException Damaged(string kind) => new($"This {kind} is damaged or incomplete, so it cannot be shown. Try another copy of the file.");
    private static DocumentException Unsupported(string kind) =>
        new($"This {kind} uses an old kind of password protection this viewer cannot open. Open it in LibreOffice and save it again to view it here.");
}
