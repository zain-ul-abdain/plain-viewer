using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
namespace PlainViewer.Core;

// A file needs its password (Incorrect: the one given did not open it). The app asks and opens the file again.
public sealed class PasswordException(string message, bool incorrect) : DocumentException(message)
{
    public bool Incorrect { get; } = incorrect;
}

// Password-protected Word, Excel and PowerPoint files (.docx, .xlsx, .pptx and their variants): an OLE compound file
// holding EncryptionInfo and EncryptedPackage, the encrypted ZIP package. Decrypted in the worker's memory with the
// password the user typed, following Microsoft's [MS-OFFCRYPTO]: Agile encryption (Office 2010 and later, section
// 2.3.4.10) and Standard encryption (Office 2007, section 2.3.4.5). The password is passed to the worker on its
// standard input, used here and never written anywhere; nothing is logged.
public static class OfficeEncryption
{
    // Set once by the worker from its standard input, for the one document it opens.
    public static string? Password { get; set; }

    public static bool IsEncrypted(byte[] bytes) =>
        CompoundFile.IsCompoundFile(bytes) && new CompoundFile(bytes, "file") is var file && file.Has("EncryptionInfo") && file.Has("EncryptedPackage");

    public static string Required(string kind) => $"This {kind} is protected with a password. Enter its password to view it.";
    public static string Incorrect(string kind) => $"That password does not open this {kind}. Check it and try again; passwords are case-sensitive.";

    // The decrypted package, or a PasswordException when no password or a wrong one was given.
    public static byte[] Decrypt(byte[] bytes, string kind)
    {
        var file = new CompoundFile(bytes, kind);
        var info = file.Read(file.Find("EncryptionInfo") ?? throw Damaged(kind), kind);
        var package = file.Read(file.Find("EncryptedPackage") ?? throw Damaged(kind), kind);
        if (info.Length < 8 || package.Length < 8) throw Damaged(kind);
        if (Password is not { Length: > 0 } password) throw new PasswordException(Required(kind), false);
        int major = BinaryPrimitives.ReadUInt16LittleEndian(info), minor = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(2));
        long size = BinaryPrimitives.ReadInt64LittleEndian(package);
        if (size < 0 || size > package.Length) throw Damaged(kind);
        byte[] plain = (major, minor) switch
        {
            (4, 4) => Agile(info, package, password, kind),
            (2 or 3 or 4, 2) => Standard(info, package, password, kind),
            _ => throw new DocumentException($"This {kind} uses a kind of password protection this viewer cannot open. Remove the password in Office, or ask the sender for an unprotected copy.")
        };
        if (plain.Length < size) throw Damaged(kind);
        Array.Resize(ref plain, (int)size);
        if (!plain.AsSpan().StartsWith("PK\u0003\u0004"u8)) throw Damaged(kind);
        return plain;
    }

    private static DocumentException Damaged(string kind) => new($"This {kind} is damaged or incomplete, so it cannot be shown. Try another copy of the file.");

    // ---- Agile encryption ----

    private static readonly byte[] VerifierInputBlock = [0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79];
    private static readonly byte[] VerifierValueBlock = [0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e];
    private static readonly byte[] KeyValueBlock = [0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6];
    private const string PasswordUri = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

    private sealed record Parameters(byte[] Salt, int BlockSize, int KeyBits, int HashSize, string Hash, string Cipher, string Chaining)
    {
        public static Parameters From(XmlElement e, string kind)
        {
            try
            {
                return new(Convert.FromBase64String(e.GetAttribute("saltValue")), int.Parse(e.GetAttribute("blockSize")), int.Parse(e.GetAttribute("keyBits")),
                    int.Parse(e.GetAttribute("hashSize")), e.GetAttribute("hashAlgorithm"), e.GetAttribute("cipherAlgorithm"), e.GetAttribute("cipherChaining"));
            }
            catch (FormatException) { throw Damaged(kind); }
        }
    }

    private static byte[] Agile(byte[] info, byte[] package, string password, string kind)
    {
        var xml = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(info, 8, info.Length - 8), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            xml.Load(reader);
        }
        catch (XmlException) { throw Damaged(kind); }
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("e", "http://schemas.microsoft.com/office/2006/encryption");
        ns.AddNamespace("p", PasswordUri);
        if (xml.SelectSingleNode("/e:encryption/e:keyData", ns) is not XmlElement keyDataElement ||
            xml.SelectSingleNode($"/e:encryption/e:keyEncryptors/e:keyEncryptor[@uri='{PasswordUri}']/p:encryptedKey", ns) is not XmlElement encryptedKey)
            throw new DocumentException($"This {kind} is protected with a certificate rather than a password, which this viewer cannot open.");
        var keyData = Parameters.From(keyDataElement, kind);
        var passwordKey = Parameters.From(encryptedKey, kind);
        if (!int.TryParse(encryptedKey.GetAttribute("spinCount"), out int spinCount) || spinCount is < 0 or > 10_000_000) throw Damaged(kind);
        foreach (var p in new[] { keyData, passwordKey })
            if (p.Cipher != "AES" || p.Chaining != "ChainingModeCBC" || p.BlockSize != 16 || p.KeyBits is not (128 or 192 or 256) || HashOf(p.Hash) is null || p.Salt.Length is < 1 or > 64)
                throw new DocumentException($"This {kind} uses a kind of password protection this viewer cannot open. Remove the password in Office, or ask the sender for an unprotected copy.");

        // [MS-OFFCRYPTO] 2.3.4.11: the password's hash, iterated spinCount times, then one key per purpose.
        var hash = HashOf(passwordKey.Hash)!;
        byte[] h = hash(Concat(passwordKey.Salt, Encoding.Unicode.GetBytes(password)));
        byte[] round = new byte[4 + h.Length];
        for (int i = 0; i < spinCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(round, i);
            h.CopyTo(round, 4);
            h = hash(round);
        }
        byte[] Key(byte[] block) => Fit(hash(Concat(h, block)), passwordKey.KeyBits / 8, 0x36);
        byte[] iv = Fit(passwordKey.Salt, passwordKey.BlockSize, 0x36);
        byte[] DecryptValue(byte[] block, string attribute)
        {
            byte[] value;
            try { value = Convert.FromBase64String(encryptedKey.GetAttribute(attribute)); } catch (FormatException) { throw Damaged(kind); }
            if (value.Length == 0 || value.Length % 16 != 0) throw Damaged(kind);
            return Cbc(Key(block), iv, value);
        }
        byte[] verifier = DecryptValue(VerifierInputBlock, "encryptedVerifierHashInput");
        byte[] expected = DecryptValue(VerifierValueBlock, "encryptedVerifierHashValue");
        byte[] actual = hash(verifier.AsSpan(0, Math.Min(verifier.Length, passwordKey.Salt.Length)).ToArray());
        if (expected.Length < passwordKey.HashSize || actual.Length < passwordKey.HashSize ||
            !CryptographicOperations.FixedTimeEquals(actual.AsSpan(0, passwordKey.HashSize), expected.AsSpan(0, passwordKey.HashSize)))
            throw new PasswordException(Incorrect(kind), true);
        byte[] secret = DecryptValue(KeyValueBlock, "encryptedKeyValue").AsSpan(0, passwordKey.KeyBits / 8).ToArray();
        var dataHash = HashOf(keyData.Hash)!;

        // 2.3.4.14: data integrity, an HMAC of the whole EncryptedPackage stream; its key and value are encrypted with the
        // secret key. A file changed after it was protected is refused rather than shown. (Files without it are allowed.)
        if (xml.SelectSingleNode("/e:encryption/e:dataIntegrity", ns) is XmlElement integrity)
        {
            byte[] Decrypt(string attribute, byte[] block)
            {
                byte[] value;
                try { value = Convert.FromBase64String(integrity.GetAttribute(attribute)); } catch (FormatException) { throw Damaged(kind); }
                if (value.Length == 0 || value.Length % 16 != 0) throw Damaged(kind);
                byte[] iv = Fit(dataHash(Concat(keyData.Salt, block)), keyData.BlockSize, 0x36);
                return Cbc(Fit(secret, keyData.KeyBits / 8, 0), iv, value).AsSpan(0, Math.Min(value.Length, keyData.HashSize)).ToArray();
            }
            byte[] hmacKey = Decrypt("encryptedHmacKey", [0x5f, 0xb2, 0xad, 0x01, 0x0c, 0xb9, 0xe1, 0xf6]);
            byte[] hmacValue = Decrypt("encryptedHmacValue", [0xa0, 0x67, 0x7f, 0x02, 0xb2, 0x2c, 0x84, 0x33]);
            byte[] computed = keyData.Hash switch
            {
                "SHA1" => HMACSHA1.HashData(hmacKey, package), "SHA256" => HMACSHA256.HashData(hmacKey, package),
                "SHA384" => HMACSHA384.HashData(hmacKey, package), _ => HMACSHA512.HashData(hmacKey, package)
            };
            if (computed.Length != hmacValue.Length || !CryptographicOperations.FixedTimeEquals(computed, hmacValue))
                throw new DocumentException($"This {kind} was changed after it was protected with its password, so it cannot be shown safely. Ask the sender for a fresh copy.");
        }

        // 2.3.4.15: the package in 4,096-byte segments, each with its own initialisation vector.
        var plain = new byte[package.Length - 8];
        byte[] segmentIndex = new byte[4];
        using var aes = Aes.Create(); aes.Key = secret.Length == keyData.KeyBits / 8 ? secret : Fit(secret, keyData.KeyBits / 8, 0);
        for (int offset = 0, segment = 0; offset < plain.Length; offset += 4096, segment++)
        {
            int length = Math.Min(4096, plain.Length - offset);
            int padded = (length + 15) / 16 * 16;
            if (8 + offset + padded > package.Length) padded = length / 16 * 16;
            if (padded == 0) break;
            BinaryPrimitives.WriteInt32LittleEndian(segmentIndex, segment);
            byte[] segmentIv = Fit(dataHash(Concat(keyData.Salt, segmentIndex)), keyData.BlockSize, 0x36);
            aes.DecryptCbc(package.AsSpan(8 + offset, padded), segmentIv, plain.AsSpan(offset), PaddingMode.None);
        }
        return plain;
    }

    // ---- Standard encryption (Office 2007: AES in ECB mode, SHA-1, 50,000 iterations) ----

    private static byte[] Standard(byte[] info, byte[] package, string password, string kind)
    {
        int flags = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(4));
        if ((flags & 0x24) != 0x24) throw new DocumentException($"This {kind} uses a kind of password protection this viewer cannot open. Remove the password in Office, or ask the sender for an unprotected copy.");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(8));
        if (headerSize < 32 || 12 + headerSize + 4 + 16 + 16 + 4 + 32 > info.Length) throw Damaged(kind);
        int keyBits = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(12 + 16));
        if (keyBits == 0) keyBits = 128;
        if (keyBits is not (128 or 192 or 256)) throw Damaged(kind);
        int verifierStart = 12 + headerSize;
        if (BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(verifierStart)) != 16) throw Damaged(kind);
        byte[] salt = info.AsSpan(verifierStart + 4, 16).ToArray();
        byte[] encryptedVerifier = info.AsSpan(verifierStart + 20, 16).ToArray();
        byte[] encryptedVerifierHash = info.AsSpan(verifierStart + 40, 32).ToArray();

        // 2.3.4.7: key derivation.
        byte[] h = SHA1.HashData(Concat(salt, Encoding.Unicode.GetBytes(password)));
        byte[] round = new byte[24];
        for (int i = 0; i < 50000; i++) { BinaryPrimitives.WriteInt32LittleEndian(round, i); h.CopyTo(round, 4); h = SHA1.HashData(round); }
        byte[] final = SHA1.HashData(Concat(h, new byte[4]));
        byte[] Derive(byte fill) { var buffer = Enumerable.Repeat(fill, 64).ToArray(); for (int i = 0; i < final.Length; i++) buffer[i] ^= final[i]; return SHA1.HashData(buffer); }
        byte[] key = Concat(Derive(0x36), Derive(0x5c)).AsSpan(0, keyBits / 8).ToArray();

        using var aes = Aes.Create(); aes.Key = key;
        byte[] verifier = aes.DecryptEcb(encryptedVerifier, PaddingMode.None);
        byte[] verifierHash = aes.DecryptEcb(encryptedVerifierHash, PaddingMode.None);
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(verifier), verifierHash.AsSpan(0, 20)))
            throw new PasswordException(Incorrect(kind), true);
        int length = (package.Length - 8) / 16 * 16;
        return aes.DecryptEcb(package.AsSpan(8, length), PaddingMode.None);
    }

    // ---- Helpers ----

    private static Func<byte[], byte[]>? HashOf(string name) => name switch
    {
        "SHA1" => SHA1.HashData, "SHA256" => SHA256.HashData, "SHA384" => SHA384.HashData, "SHA512" => SHA512.HashData, _ => null
    };

    private static byte[] Concat(byte[] a, byte[] b) { var result = new byte[a.Length + b.Length]; a.CopyTo(result, 0); b.CopyTo(result, a.Length); return result; }

    // Truncated, or padded with `fill`, to `length` bytes.
    private static byte[] Fit(byte[] value, int length, byte fill)
    {
        var result = Enumerable.Repeat(fill, length).ToArray();
        value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(result);
        return result;
    }

    private static byte[] Cbc(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    // Test aid (tests/PlainViewer.Tests): encrypts a package with Standard encryption, the inverse of Standard above.
    internal static (byte[] Info, byte[] Package) EncryptStandardForTests(byte[] plain, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] h = SHA1.HashData(Concat(salt, Encoding.Unicode.GetBytes(password)));
        byte[] round = new byte[24];
        for (int i = 0; i < 50000; i++) { BinaryPrimitives.WriteInt32LittleEndian(round, i); h.CopyTo(round, 4); h = SHA1.HashData(round); }
        byte[] final = SHA1.HashData(Concat(h, new byte[4]));
        byte[] Derive(byte fill) { var buffer = Enumerable.Repeat(fill, 64).ToArray(); for (int i = 0; i < final.Length; i++) buffer[i] ^= final[i]; return SHA1.HashData(buffer); }
        byte[] key = Concat(Derive(0x36), Derive(0x5c)).AsSpan(0, 16).ToArray();
        using var aes = Aes.Create(); aes.Key = key;
        byte[] verifier = RandomNumberGenerator.GetBytes(16);
        byte[] verifierHash = Fit(SHA1.HashData(verifier), 32, 0);
        var header = new byte[32 + 2];                                    // EncryptionHeader with a two-byte empty CSP name
        BinaryPrimitives.WriteInt32LittleEndian(header, 0x24);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 0x660E);       // AES-128
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 0x8004);      // SHA-1
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 128);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), 0x18);
        var info = new MemoryStream();
        info.Write([4, 0, 2, 0, 0x24, 0, 0, 0]);
        info.Write(BitConverter.GetBytes(header.Length)); info.Write(header);
        info.Write(BitConverter.GetBytes(16)); info.Write(salt);
        info.Write(aes.EncryptEcb(verifier, PaddingMode.None));
        info.Write(BitConverter.GetBytes(20)); info.Write(aes.EncryptEcb(verifierHash, PaddingMode.None));
        var padded = Fit(plain, (plain.Length + 15) / 16 * 16, 0);
        var package = new MemoryStream();
        package.Write(BitConverter.GetBytes((long)plain.Length)); package.Write(aes.EncryptEcb(padded, PaddingMode.None));
        return (info.ToArray(), package.ToArray());
    }

    // Test aid: decrypts streams that are already out of their compound file.
    internal static byte[] DecryptStreamsForTests(byte[] info, byte[] package, string password, string kind)
    {
        int major = BinaryPrimitives.ReadUInt16LittleEndian(info), minor = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(2));
        long size = BinaryPrimitives.ReadInt64LittleEndian(package);
        byte[] plain = (major, minor) is (4, 4) ? Agile(info, package, password, kind) : Standard(info, package, password, kind);
        Array.Resize(ref plain, (int)size);
        return plain;
    }
}
