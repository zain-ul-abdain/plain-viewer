using System.Buffers.Binary;
using System.Numerics;
namespace PlainViewer.Core;

// Argon2id (RFC 9106, version 0x13) and the BLAKE2b hash it is built on (RFC 7693), for LibreOffice's password
// protection of OpenDocument files (OpenDocumentEncryption). Neither is in .NET. Checked against the RFCs' test vectors
// (tests/PlainViewer.Tests). Single-threaded: lanes are computed one after another, which gives the same result.
public static class Argon2
{
    private const int BlockWords = 128;   // 1 KiB blocks of 64-bit words

    public static byte[] Hash(byte[] password, byte[] salt, int iterations, int memoryKiB, int lanes, int length, byte[]? secret = null, byte[]? data = null)
    {
        if (lanes is < 1 or > 64 || iterations is < 1 or > 16 || length is < 4 or > 1024 || memoryKiB < 8 * lanes || memoryKiB > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(memoryKiB), "Argon2 parameters out of range.");
        secret ??= []; data ??= [];
        // H0: the parameters and inputs, each length-prefixed.
        var h0Input = new List<byte>();
        void Int(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); h0Input.AddRange(b.ToArray()); }
        void Bytes(byte[] value) { Int(value.Length); h0Input.AddRange(value); }
        Int(lanes); Int(length); Int(memoryKiB); Int(iterations); Int(0x13); Int(2);   // type 2 = Argon2id
        Bytes(password); Bytes(salt); Bytes(secret); Bytes(data);
        byte[] h0 = Blake2b.Hash(h0Input.ToArray(), 64);

        int segmentLength = memoryKiB / (4 * lanes), laneLength = segmentLength * 4;
        var memory = new ulong[lanes * laneLength][];
        for (int l = 0; l < lanes; l++)
            for (int i = 0; i < 2; i++)
            {
                var input = new byte[72]; h0.CopyTo(input, 0);
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(64), i); BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(68), l);
                memory[l * laneLength + i] = ToWords(LongHash(input, 1024));
            }

        for (int pass = 0; pass < iterations; pass++)
            for (int slice = 0; slice < 4; slice++)
                for (int lane = 0; lane < lanes; lane++)
                    FillSegment(memory, pass, lane, slice, lanes, laneLength, segmentLength, iterations, memoryKiB);

        var final = (ulong[])memory[laneLength - 1].Clone();
        for (int l = 1; l < lanes; l++) { var last = memory[l * laneLength + laneLength - 1]; for (int w = 0; w < BlockWords; w++) final[w] ^= last[w]; }
        return LongHash(ToBytes(final), length);
    }

    private static void FillSegment(ulong[][] memory, int pass, int lane, int slice, int lanes, int laneLength, int segmentLength, int iterations, int memoryKiB)
    {
        bool dataIndependent = pass == 0 && slice < 2;   // Argon2id: the first half of the first pass uses Argon2i addressing
        ulong[]? addresses = null, inputBlock = null;
        int start = pass == 0 && slice == 0 ? 2 : 0;
        if (dataIndependent)
        {
            inputBlock = new ulong[BlockWords];
            inputBlock[0] = (ulong)pass; inputBlock[1] = (ulong)lane; inputBlock[2] = (ulong)slice;
            inputBlock[3] = (ulong)(lanes * laneLength); inputBlock[4] = (ulong)iterations; inputBlock[5] = 2;
            addresses = new ulong[BlockWords];
            if (start == 2) NextAddresses(inputBlock, addresses);
        }
        for (int index = start; index < segmentLength; index++)
        {
            int current = lane * laneLength + slice * segmentLength + index;
            int previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;
            ulong pseudoRandom;
            if (dataIndependent)
            {
                if (index % BlockWords == 0) NextAddresses(inputBlock!, addresses!);
                pseudoRandom = addresses![index % BlockWords];
            }
            else pseudoRandom = memory[previous][0];
            int refLane = pass == 0 && slice == 0 ? lane : (int)((pseudoRandom >> 32) % (ulong)lanes);
            int refIndex = ReferenceIndex(pass, slice, index, refLane == lane, (uint)pseudoRandom, laneLength, segmentLength);
            var reference = memory[refLane * laneLength + refIndex];
            var result = Compress(memory[previous], reference);
            if (pass > 0 && memory[current] is { } old) for (int w = 0; w < BlockWords; w++) result[w] ^= old[w];
            memory[current] = result;
        }
    }

    private static void NextAddresses(ulong[] inputBlock, ulong[] addresses)
    {
        inputBlock[6]++;
        var zero = new ulong[BlockWords];
        var tmp = Compress(zero, inputBlock);
        var result = Compress(zero, tmp);
        result.CopyTo(addresses, 0);
    }

    // RFC 9106 section 3.4.1.2: which earlier block of the reference lane is mixed in.
    private static int ReferenceIndex(int pass, int slice, int index, bool sameLane, uint pseudoRandom, int laneLength, int segmentLength)
    {
        long areaSize;
        if (pass == 0) areaSize = slice == 0 ? index - 1 : sameLane ? slice * segmentLength + index - 1 : slice * segmentLength + (index == 0 ? -1 : 0);
        else areaSize = sameLane ? laneLength - segmentLength + index - 1 : laneLength - segmentLength + (index == 0 ? -1 : 0);
        ulong relative = pseudoRandom;
        relative = (relative * relative) >> 32;
        relative = (ulong)areaSize - 1 - (((ulong)areaSize * relative) >> 32);
        long startPosition = pass != 0 && slice != 3 ? (slice + 1) * segmentLength : 0;
        return (int)((startPosition + (long)relative) % laneLength);
    }

    // The compression function G (RFC 9106 section 3.5), on two blocks.
    private static ulong[] Compress(ulong[] x, ulong[] y)
    {
        var r = new ulong[BlockWords];
        for (int i = 0; i < BlockWords; i++) r[i] = x[i] ^ y[i];
        var q = (ulong[])r.Clone();
        for (int i = 0; i < 8; i++)        // rows
            Permute(q, 16 * i, 16 * i + 1, 16 * i + 2, 16 * i + 3, 16 * i + 4, 16 * i + 5, 16 * i + 6, 16 * i + 7,
                16 * i + 8, 16 * i + 9, 16 * i + 10, 16 * i + 11, 16 * i + 12, 16 * i + 13, 16 * i + 14, 16 * i + 15);
        for (int i = 0; i < 8; i++)        // columns
            Permute(q, 2 * i, 2 * i + 1, 2 * i + 16, 2 * i + 17, 2 * i + 32, 2 * i + 33, 2 * i + 48, 2 * i + 49,
                2 * i + 64, 2 * i + 65, 2 * i + 80, 2 * i + 81, 2 * i + 96, 2 * i + 97, 2 * i + 112, 2 * i + 113);
        for (int i = 0; i < BlockWords; i++) q[i] ^= r[i];
        return q;
    }

    private static void Permute(ulong[] v, int a0, int a1, int a2, int a3, int a4, int a5, int a6, int a7, int a8, int a9, int a10, int a11, int a12, int a13, int a14, int a15)
    {
        Mix(v, a0, a4, a8, a12); Mix(v, a1, a5, a9, a13); Mix(v, a2, a6, a10, a14); Mix(v, a3, a7, a11, a15);
        Mix(v, a0, a5, a10, a15); Mix(v, a1, a6, a11, a12); Mix(v, a2, a7, a8, a13); Mix(v, a3, a4, a9, a14);
    }

    private static ulong Mul(ulong a, ulong b) => 2 * (a & 0xFFFFFFFF) * (b & 0xFFFFFFFF);

    private static void Mix(ulong[] v, int a, int b, int c, int d)
    {
        v[a] = v[a] + v[b] + Mul(v[a], v[b]); v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d] + Mul(v[c], v[d]); v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + Mul(v[a], v[b]); v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d] + Mul(v[c], v[d]); v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    // H' (RFC 9106 section 3.3): BLAKE2b extended to any output length.
    private static byte[] LongHash(byte[] input, int length)
    {
        var prefixed = new byte[4 + input.Length];
        BinaryPrimitives.WriteInt32LittleEndian(prefixed, length); input.CopyTo(prefixed, 4);
        if (length <= 64) return Blake2b.Hash(prefixed, length);
        var result = new byte[length];
        byte[] v = Blake2b.Hash(prefixed, 64);
        int at = 0;
        while (length - at > 64) { v.AsSpan(0, 32).CopyTo(result.AsSpan(at)); at += 32; if (length - at > 64) v = Blake2b.Hash(v, 64); }
        Blake2b.Hash(v, length - at).CopyTo(result, at);
        return result;
    }

    private static ulong[] ToWords(byte[] bytes) { var words = new ulong[BlockWords]; for (int i = 0; i < BlockWords; i++) words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8)); return words; }
    private static byte[] ToBytes(ulong[] words) { var bytes = new byte[words.Length * 8]; for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), words[i]); return bytes; }
}

// BLAKE2b (RFC 7693), unkeyed, output 1 to 64 bytes.
public static class Blake2b
{
    private static readonly ulong[] IV =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];
    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4], [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13], [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11], [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5], [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    public static byte[] Hash(byte[] input, int length)
    {
        if (length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(length));
        var h = (ulong[])IV.Clone();
        h[0] ^= 0x01010000UL ^ (ulong)length;
        var block = new byte[128];
        ulong counter = 0;
        int at = 0;
        while (input.Length - at > 128)
        {
            counter += 128;
            Compress(h, input.AsSpan(at, 128), counter, false);
            at += 128;
        }
        Array.Clear(block);
        input.AsSpan(at).CopyTo(block);
        counter += (ulong)(input.Length - at);
        Compress(h, block, counter, true);
        var output = new byte[64];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(i * 8), h[i]);
        return output.AsSpan(0, length).ToArray();
    }

    private static void Compress(ulong[] h, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 8; i++) { v[i] = h[i]; v[i + 8] = IV[i]; }
        v[12] ^= counter;
        if (last) v[14] = ~v[14];
        for (int round = 0; round < 12; round++)
        {
            var s = Sigma[round % 10];
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]); G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]); G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]); G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]); G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (int i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
