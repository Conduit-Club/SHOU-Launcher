using System.Buffers.Binary;
using System.IO.Compression;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Tools;

public static class ToolImages
{
    public static byte[] Avatar(AccountTextureImage skin, int size)
    {
        if (size is not (64 or 128 or 256)) throw new ArgumentException("头像大小须为 64、128 或 256。");
        var source = skin.Rgba.Span; var rgba = new byte[size * size * 4];
        // Old 64×32 skins often have an opaque, unused hat rectangle; treat the solid rectangle as absent.
        var uniformHat = true; var first = source.Slice((8 * skin.Width + 40) * 4, 4);
        for (var y = 8; y < 16; y++) for (var x = 40; x < 48; x++) if (!source.Slice((y * skin.Width + x) * 4, 4).SequenceEqual(first)) uniformHat = false;
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var sx = x * 8 / size; var sy = y * 8 / size; var target = rgba.AsSpan((y * size + x) * 4, 4);
            var face = source.Slice(((8 + sy) * skin.Width + 8 + sx) * 4, 4); face.CopyTo(target);
            if (skin.Height != 32 || !uniformHat) Composite(source.Slice(((8 + sy) * skin.Width + 40 + sx) * 4, 4), target);
        }
        return EncodeRgba(size, size, rgba);
    }
    private static void Composite(ReadOnlySpan<byte> over, Span<byte> under)
    {
        var a = over[3]; var b = under[3] * (255 - a); var alpha = a * 255 + b;
        if (alpha == 0) { under.Clear(); return; }
        for (var c = 0; c < 3; c++) under[c] = (byte)((over[c] * a * 255 + under[c] * b + alpha / 2) / alpha);
        under[3] = (byte)((alpha + 127) / 255);
    }
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || rgba.Length != checked(width * height * 4)) throw new ArgumentException("图像像素大小无效。");
        using var png = new MemoryStream(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13]; header.Clear(); BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header[4..], height); header[8] = 8; header[9] = 6; Chunk(png, "IHDR"u8, header);
        using var packed = new MemoryStream();
        using (var zip = new ZLibStream(packed, CompressionLevel.Fastest, true)) for (var y = 0; y < height; y++) { zip.WriteByte(0); zip.Write(rgba.Slice(y * width * 4, width * 4)); }
        Chunk(png, "IDAT"u8, packed.ToArray()); Chunk(png, "IEND"u8, []); return png.ToArray();
    }
    private static void Chunk(Stream output, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> bytes)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); output.Write(number); output.Write(tag); output.Write(bytes);
        var crc = uint.MaxValue; foreach (var b in tag) Update(ref crc, b); foreach (var b in bytes) Update(ref crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
    private static void Update(ref uint crc, byte b) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : crc >> 1 ^ 0xedb88320u; }
}
