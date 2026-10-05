using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace PlainViewer.Core;

// Password protection of the older binary formats (.xls, .doc), [MS-OFFCRYPTO] 2.3.6 "RC4 encryption" (Office 97 and
// 2000, and what LibreOffice writes) and 2.3.5 "RC4 CryptoAPI encryption" (Office 2002 and later). Both encrypt a stream
// with RC4, rekeyed every block (1,024 bytes in Excel, 512 in Word) from a key derived from the password; positions
// count from the start of the stream, so bytes left unencrypted still use up their share of the key stream. RC4 is
// not in .NET, so it is implemented here; the hashes are .NET's. The password is never stored. The weaker "XOR
// obfuscation" of Excel 95-era files is not supported.
public static class LegacyEncryption
{
    // A key for each block of a stream, once the password has been checked against the file's verifier.
    public sealed class Key
    {
        private readonly Func<int, byte[]> blockKey;
        internal Key(Func<int, byte[]> blockKey) => this.blockKey = blockKey;
        internal byte[] BlockKey(int block) => blockKey(block);

        // Decrypts `bytes` in place at the given stream positions; `skip(position)` marks bytes that are not encrypted.
        public void Decrypt(byte[] bytes, int blockSize, int from, int to, Func<int, bool>? skip = null)
        {
            Rc4? rc4 = null; int block = -1, at = 0;
            for (int position = from; position < to; position++)
            {
                int wanted = position / blockSize, offset = position % blockSize;
                if (wanted != block || offset < at) { rc4 = new Rc4(blockKey(wanted)); block = wanted; at = 0; }
                while (at < offset) { rc4!.Next(); at++; }
                byte stream = rc4!.Next(); at++;
                if (skip is null || !skip(position)) bytes[position] ^= stream;
            }
        }
    }

    // Reads an encryption header ([MS-OFFCRYPTO] 2.3.6.1 or 2.3.5.1, starting with its version) and checks the
    // password. Throws PasswordException when there is none or it is wrong.
    public static Key Open(ReadOnlySpan<byte> header, string kind)
    {
        if (header.Length < 4) throw Damaged(kind);
        string password = OfficeEncryption.PasswordOrDefault;    // Excel's fixed password first when none was typed
        int major = BinaryPrimitives.ReadUInt16LittleEndian(header), minor = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        try
        {
            if (major == 1 && minor == 1) return Rc4Binary(header[4..], password, kind);
            if (major is 2 or 3 or 4 && minor == 2) return Rc4CryptoApi(header[4..], password, kind);
        }
        catch (PasswordException) when (!OfficeEncryption.PasswordGiven) { throw new PasswordException(OfficeEncryption.Required(kind), false); }
        throw Unsupported(kind);
    }

    private static DocumentException Damaged(string kind) => new($"This {kind} is damaged or incomplete, so it cannot be shown. Try another copy of the file.");
    public static DocumentException Unsupported(string kind) =>
        new($"This {kind} uses a kind of password protection this viewer cannot open. Remove the password in the application that made it, or ask the sender for an unprotected copy.");

    // 2.3.6.2: MD5 of the password, cut to 40 bits, mixed with the salt; each block key is MD5 of that and the block number.
    private static Key Rc4Binary(ReadOnlySpan<byte> rest, string password, string kind)
    {
        if (rest.Length < 48) throw Damaged(kind);
        byte[] salt = rest[..16].ToArray(), verifier = rest.Slice(16, 16).ToArray(), verifierHash = rest.Slice(32, 16).ToArray();
        byte[] h0 = MD5.HashData(Encoding.Unicode.GetBytes(password));
        var buffer = new byte[21 * 16];
        for (int i = 0; i < 16; i++) { h0.AsSpan(0, 5).CopyTo(buffer.AsSpan(i * 21)); salt.CopyTo(buffer, i * 21 + 5); }
        byte[] h1 = MD5.HashData(buffer).AsSpan(0, 5).ToArray();
        byte[] BlockKey(int block)
        {
            var input = new byte[9]; h1.CopyTo(input, 0); BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(5), block);
            return MD5.HashData(input);
        }
        var rc4 = new Rc4(BlockKey(0));
        byte[] plainVerifier = rc4.Apply(verifier), plainHash = rc4.Apply(verifierHash);
        if (!CryptographicOperations.FixedTimeEquals(MD5.HashData(plainVerifier), plainHash)) throw new PasswordException(OfficeEncryption.Incorrect(kind), true);
        return new Key(BlockKey);
    }

    // 2.3.5.2: SHA-1 of the salt and password; each block key is SHA-1 of that and the block number, cut to the key size.
    private static Key Rc4CryptoApi(ReadOnlySpan<byte> rest, string password, string kind)
    {
        // Flags (4), header size (4), header (AlgID at 8, AlgIDHash at 12, KeySize at 16), then the verifier.
        if (rest.Length < 8) throw Damaged(kind);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(rest[4..]);
        if (headerSize < 32 || 8L + headerSize + 4 + 16 + 16 + 4 + 20 > rest.Length) throw Damaged(kind);
        var header = rest.Slice(8, headerSize);
        int algorithm = BinaryPrimitives.ReadInt32LittleEndian(header[8..]), hash = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int keyBits = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (keyBits == 0) keyBits = 40;
        if ((algorithm is not (0x6801 or 0)) || (hash is not (0x8004 or 0)) || keyBits is < 40 or > 128 || keyBits % 8 != 0) throw Unsupported(kind);
        var verifierPart = rest[(8 + headerSize)..];
        if (BinaryPrimitives.ReadInt32LittleEndian(verifierPart) != 16) throw Damaged(kind);
        byte[] salt = verifierPart.Slice(4, 16).ToArray(), verifier = verifierPart.Slice(20, 16).ToArray();
        int hashSize = BinaryPrimitives.ReadInt32LittleEndian(verifierPart[36..]);
        if (hashSize != 20 || verifierPart.Length < 40 + 20) throw Damaged(kind);
        byte[] verifierHash = verifierPart.Slice(40, 20).ToArray();
        byte[] h0 = SHA1.HashData(Concat(salt, Encoding.Unicode.GetBytes(password)));
        byte[] BlockKey(int block)
        {
            var input = new byte[24]; h0.CopyTo(input, 0); BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(20), block);
            var key = new byte[keyBits == 40 ? 16 : keyBits / 8];         // a 40-bit key is padded with zeros to 128 bits
            SHA1.HashData(input).AsSpan(0, keyBits / 8).CopyTo(key);
            return key;
        }
        var rc4 = new Rc4(BlockKey(0));
        byte[] plainVerifier = rc4.Apply(verifier), plainHash = rc4.Apply(verifierHash);
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(plainVerifier), plainHash)) throw new PasswordException(OfficeEncryption.Incorrect(kind), true);
        return new Key(BlockKey);
    }

    private static byte[] Concat(byte[] a, byte[] b) { var result = new byte[a.Length + b.Length]; a.CopyTo(result, 0); b.CopyTo(result, a.Length); return result; }

    // ---- Excel 97-2003 (.xls) ----

    // [MS-XLS] 2.2.10: everything after the FILEPASS record is encrypted except record headers, the BOF, FILEPASS,
    // USREXCL, FILELOCK, INTERFACEHDR, RRDINFO and RRDHEAD records, and the stream position (lbPlyPos) at the start of
    // each BOUNDSHEET8 record. Returns the workbook stream decrypted; the FILEPASS record itself is left as it is.
    public static byte[] DecryptWorkbook(byte[] data, int filepassData, int filepassLength)
    {
        const string kind = "workbook";
        var record = data.AsSpan(filepassData, filepassLength);
        if (record.Length < 2) throw Damaged(kind);
        if (BinaryPrimitives.ReadUInt16LittleEndian(record) != 1) throw Unsupported(kind);    // 0: XOR obfuscation
        var key = Open(record[2..], kind);
        var plain = (byte[])data.Clone();
        var clear = new System.Collections.BitArray(plain.Length);
        int start = filepassData + filepassLength;
        for (int at = start; at + 4 <= plain.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(at + 2));
            int body = at + 4, end = Math.Min(plain.Length, body + length);
            for (int i = at; i < Math.Min(body, plain.Length); i++) clear[i] = true;
            if (type is 0x0809 or 0x002F or 0x0194 or 0x0195 or 0x00E1 or 0x0196 or 0x0138) for (int i = body; i < end; i++) clear[i] = true;
            else if (type == 0x0085) for (int i = body; i < Math.Min(end, body + 4); i++) clear[i] = true;
            at = body + length;
        }
        key.Decrypt(plain, 1024, start, plain.Length, position => clear[position]);
        return plain;
    }

    // ---- PowerPoint 97-2003 (.ppt) ----

    public const uint EncryptedToken = 0xF3D1C4DF, PlainToken = 0xE391C05F;   // [MS-PPT] 2.3.2 CurrentUserAtom headerToken

    // [MS-PPT] 2.3.7: a protected presentation's Current User stream carries EncryptedToken. Every persist object (found
    // through the persist directories of the chain of UserEditAtoms) is encrypted on its own, from its first byte, with
    // the key for its persist ID as the block number; the UserEditAtoms, persist directories and the
    // CryptSession10Container holding the encryption header are not. Decrypts `records` (the PowerPoint Document stream)
    // in place and marks `currentUser` as not encrypted. Pictures (in the separate Pictures stream) are left as they are.
    // Files saved with PowerPoint's fast save keep older copies of changed objects, listed by older edits' persist
    // directories: every listed copy is decrypted (each with its own persist ID), or walking the records would find
    // encrypted leftovers.
    public static void DecryptPresentation(byte[] records, byte[] currentUser, string kind)
    {
        if (currentUser.Length < 20) throw Damaged(kind);
        int edit = BinaryPrimitives.ReadInt32LittleEndian(currentUser.AsSpan(16));
        // Positions come from the file: compared by subtraction so that a huge value cannot wrap around.
        int Int(int at) => at >= 0 && at <= records.Length - 4 ? BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(at)) : throw Damaged(kind);
        if (edit < 0 || edit > records.Length - 40) throw Damaged(kind);
        int length = Int(edit + 4);
        if (length < 0x20) throw Damaged(kind);
        int session = Int(edit + 8 + 28);           // encryptSessionPersistIdRef
        var newest = new Dictionary<int, int>();    // persist ID -> its newest copy's stream offset
        var copies = new Dictionary<int, int>();    // stream offset -> persist ID, every copy listed
        var seen = new HashSet<int>();
        for (int at = edit; at > 0 && seen.Add(at);)
        {
            if (at > records.Length - 40) throw Damaged(kind);
            int directory = Int(at + 8 + 12), next = Int(at + 8 + 8);
            int size = Int(directory + 4);
            if (size < 0 || (long)directory + 8 + size > records.Length) throw Damaged(kind);
            int end = directory + 8 + size;
            for (int p = directory + 8; p + 4 <= end;)
            {
                uint info = (uint)Int(p); p += 4;
                int first = (int)(info & 0xFFFFF), count = (int)(info >> 20);
                for (int i = 0; i < count && p + 4 <= end; i++, p += 4) { int offset = Int(p); newest.TryAdd(first + i, offset); copies.TryAdd(offset, first + i); }
            }
            at = next;
        }
        if (!newest.TryGetValue(session, out int sessionAt) || sessionAt < 0 || sessionAt > records.Length - 8) throw Damaged(kind);
        int sessionLength = Int(sessionAt + 4);
        if (BinaryPrimitives.ReadUInt16LittleEndian(records.AsSpan(sessionAt + 2)) != 0x2F14 || sessionLength < 0 || sessionLength > records.Length - sessionAt - 8) throw Damaged(kind);
        var key = Open(records.AsSpan(sessionAt + 8, sessionLength), kind);
        foreach (var (offset, id) in copies)
        {
            if (id == session || offset < 0 || offset > records.Length - 8) continue;
            var rc4 = new Rc4(key.BlockKey(id));
            for (int i = 0; i < 8; i++) records[offset + i] ^= rc4.Next();
            long size = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            // The password was checked already, so a size past the end means a damaged file, not a wrong password.
            if (offset + 8 + size > records.Length) throw Damaged(kind);
            for (int i = 0; i < size; i++) records[offset + 8 + i] ^= rc4.Next();
        }
        BinaryPrimitives.WriteUInt32LittleEndian(currentUser.AsSpan(12), PlainToken);
    }

    // Test aid (tests/PlainViewer.Tests): an RC4 CryptoAPI encryption header (version 4.2, 128-bit key) for `password`,
    // built the way 2.3.5 describes, so header parsing and the password check can be tested without an Office-made file.
    internal static byte[] CryptoApiHeaderForTests(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), verifier = RandomNumberGenerator.GetBytes(16);
        byte[] h0 = SHA1.HashData(Concat(salt, Encoding.Unicode.GetBytes(password)));
        var input = new byte[24]; h0.CopyTo(input, 0);                   // block 0
        var rc4 = new Rc4(SHA1.HashData(input).AsSpan(0, 16).ToArray());
        byte[] encryptedVerifier = rc4.Apply(verifier), encryptedHash = rc4.Apply(SHA1.HashData(verifier));
        var header = new byte[32 + 2];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 0x6801); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 0x8004);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 128);
        var result = new MemoryStream();
        result.Write([4, 0, 2, 0, 0x04, 0, 0, 0]); result.Write(BitConverter.GetBytes(header.Length)); result.Write(header);
        result.Write(BitConverter.GetBytes(16)); result.Write(salt); result.Write(encryptedVerifier);
        result.Write(BitConverter.GetBytes(20)); result.Write(encryptedHash);
        return result.ToArray();
    }

    // ---- RC4 ----

    internal sealed class Rc4
    {
        private readonly byte[] s = new byte[256];
        private int i, j;
        public Rc4(byte[] key)
        {
            for (int k = 0; k < 256; k++) s[k] = (byte)k;
            for (int k = 0, m = 0; k < 256; k++) { m = (m + s[k] + key[k % key.Length]) & 255; (s[k], s[m]) = (s[m], s[k]); }
        }
        public byte Next()
        {
            i = (i + 1) & 255; j = (j + s[i]) & 255; (s[i], s[j]) = (s[j], s[i]);
            return s[(s[i] + s[j]) & 255];
        }
        public byte[] Apply(byte[] data) { var result = new byte[data.Length]; for (int k = 0; k < data.Length; k++) result[k] = (byte)(data[k] ^ Next()); return result; }
    }
}
