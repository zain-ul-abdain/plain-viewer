using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
namespace PlainViewer.Core;

// Windows metafile pictures (EMF and WMF, often used for logos and charts pasted from other programs in spreadsheets).
// The page cannot show them, so the worker draws each with Windows' own GDI into a white bitmap at the picture's own
// size (at most 2,000 pixels on its longer side) and stores it as PNG. Runs in the low-integrity worker under its job
// limits; GDI only draws, it opens no files or addresses for a metafile. Returns null for anything it cannot draw.
public static class Metafiles
{
    public const int MaxSide = 2000;

    public static bool IsMetafile(ReadOnlySpan<byte> b) => IsEmf(b) || IsPlaceable(b) || IsWmf(b);
    private static bool IsEmf(ReadOnlySpan<byte> b) => b.Length >= 88 && BinaryPrimitives.ReadUInt32LittleEndian(b) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(b[40..]) == 0x464D4520;
    private static bool IsPlaceable(ReadOnlySpan<byte> b) => b.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(b) == 0x9AC6CDD7;
    private static bool IsWmf(ReadOnlySpan<byte> b) => b.Length >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(b) is 1 or 2 && BinaryPrimitives.ReadUInt16LittleEndian(b[2..]) == 9;

    // `widthHundredthsMm`/`heightHundredthsMm`: the size to use for a WMF without a placeable header (from its container).
    public static byte[]? ToPng(byte[] bytes, int widthHundredthsMm = 0, int heightHundredthsMm = 0)
    {
        if (!OperatingSystem.IsWindows() || !IsMetafile(bytes)) return null;
        IntPtr metafile = IntPtr.Zero;
        try
        {
            double widthMm, heightMm;
            if (IsEmf(bytes))
            {
                // rclFrame, in hundredths of a millimetre.
                int left = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)), top = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28));
                int right = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)), bottom = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(36));
                widthMm = (right - left) / 100.0; heightMm = (bottom - top) / 100.0;
                metafile = SetEnhMetaFileBits((uint)bytes.Length, bytes);
            }
            else
            {
                byte[] bits = bytes;
                if (IsPlaceable(bytes))
                {
                    short left = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6)), top = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8));
                    short right = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(10)), bottom = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(12));
                    int inch = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
                    if (inch <= 0) return null;
                    widthMm = Math.Abs(right - left) * 25.4 / inch; heightMm = Math.Abs(bottom - top) * 25.4 / inch;
                    bits = bytes[22..];
                }
                else { widthMm = widthHundredthsMm / 100.0; heightMm = heightHundredthsMm / 100.0; }
                if (widthMm <= 0 || heightMm <= 0) return null;
                var picture = new MetafilePict { Mode = 8, Width = (int)(widthMm * 100), Height = (int)(heightMm * 100) };   // MM_ANISOTROPIC
                metafile = SetWinMetaFileBits((uint)bits.Length, bits, IntPtr.Zero, ref picture);
            }
            if (metafile == IntPtr.Zero || widthMm <= 0 || heightMm <= 0 || widthMm > 10_000 || heightMm > 10_000) return null;
            double pixels = 96 / 25.4, scale = Math.Min(1, MaxSide / Math.Max(widthMm * pixels, heightMm * pixels));
            int width = Math.Max(1, (int)Math.Round(widthMm * pixels * scale)), height = Math.Max(1, (int)Math.Round(heightMm * pixels * scale));
            return Draw(metafile, width, height);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException) { return null; }
        finally { if (metafile != IntPtr.Zero) DeleteEnhMetaFile(metafile); }
    }

    private static byte[]? Draw(IntPtr metafile, int width, int height)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bitmap = IntPtr.Zero, old = IntPtr.Zero;
        if (dc == IntPtr.Zero) return null;
        try
        {
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };   // top-down
            bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = SelectObject(dc, bitmap);
            PatBlt(dc, 0, 0, width, height, 0x00FF0062);   // WHITENESS
            var area = new Rect { Right = width, Bottom = height };
            if (!PlayEnhMetaFile(dc, metafile, ref area)) return null;
            GdiFlush();
            var bgra = new byte[width * height * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return Png(width, height, bgra);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(dc, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    // A minimal PNG encoder: 8-bit RGB, one zlib stream, no filtering.
    public static byte[] Png(int width, int height, byte[] bgra)
    {
        var raw = new byte[(width * 3 + 1) * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int from = (y * width + x) * 4, to = y * (width * 3 + 1) + 1 + x * 3;
                raw[to] = bgra[from + 2]; raw[to + 1] = bgra[from + 1]; raw[to + 2] = bgra[from];
            }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length); output.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0); data.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed)); output.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MetafilePict { public int Mode, Width, Height; public IntPtr Handle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant; }

    [DllImport("gdi32.dll")] private static extern IntPtr SetEnhMetaFileBits(uint size, byte[] data);
    [DllImport("gdi32.dll")] private static extern IntPtr SetWinMetaFileBits(uint size, byte[] data, IntPtr reference, ref MetafilePict picture);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr metafile);
    [DllImport("gdi32.dll")] private static extern bool PlayEnhMetaFile(IntPtr dc, IntPtr metafile, ref Rect area);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
