using System.Buffers.Binary;
using System.IO.Compression;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

/// <summary>PNG 3 critical chunks, CRCs, filters, all standard sample depths and Adam7, with fixed texture budgets.
/// Ancillary metadata is never inflated or handed to a native decoder. Animated PNG is rejected.</summary>
public static class AccountAppearancePng
{
    public const int MaximumPngBytes = 2 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    public static AccountTextureImage Decode(ReadOnlyMemory<byte> input, bool cape = false)
    {
        if (input.Length is < 45 or > MaximumPngBytes || !input.Span.StartsWith(Signature)) throw Invalid("请选择有效的 PNG 纹理（最大 2 MiB）。");
        var png = input.ToArray(); var span = png.AsSpan(); var offset = 8; var chunks = 0;
        var width = 0; var height = 0; var depth = 0; var type = 0; var interlace = 0;
        byte[]? palette = null, transparency = null; var sawData = false; var endedData = false; var ended = false;
        using var data = new MemoryStream();
        while (offset < span.Length)
        {
            if (++chunks > 4096 || span.Length - offset < 12) throw Invalid("PNG 块数量或长度无效。");
            var length = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
            if (length > MaximumPngBytes || length > span.Length - offset - 12) throw Invalid("PNG 块超过边界。");
            var size = (int)length; var tag = span.Slice(offset + 4, 4); var body = span.Slice(offset + 8, size);
            if (!tag.ToArray().All(b => b is >= 65 and <= 90 or >= 97 and <= 122) || (tag[2] & 32) != 0) throw Invalid("PNG 块类型无效。");
            if (Crc(span.Slice(offset + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 8 + size, 4))) throw Invalid("PNG 校验失败，原文件未修改。");
            if (chunks == 1 && !tag.SequenceEqual("IHDR"u8)) throw Invalid("PNG 缺少首部。");
            if (sawData && !tag.SequenceEqual("IDAT"u8)) endedData = true;
            if (tag.SequenceEqual("IHDR"u8))
            {
                if (chunks != 1 || size != 13) throw Invalid("PNG 首部无效。");
                var w = BinaryPrimitives.ReadUInt32BigEndian(body); var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (cape ? w is < 64 or > 1024 || (w & (w - 1)) != 0 || h != w / 2 : w != 64 || h is not (32 or 64))
                    throw Invalid(cape ? "披风须为 64×32 至 1024×512 的二倍宽 PNG。" : "Java 皮肤须为 64×64 或 64×32 PNG。");
                width = (int)w; height = (int)h; depth = body[8]; type = body[9]; interlace = body[12];
                if (body[10] != 0 || body[11] != 0 || interlace > 1 || !ValidDepth(type, depth)) throw Invalid("PNG 颜色、压缩或交错格式无效。");
            }
            else if (tag.SequenceEqual("PLTE"u8))
            {
                if (palette is not null || sawData || size is < 3 or > 768 || size % 3 != 0 || type is 0 or 4 || type == 3 && size / 3 > 1 << depth) throw Invalid("PNG 调色板无效。");
                palette = body.ToArray();
            }
            else if (tag.SequenceEqual("tRNS"u8))
            {
                if (transparency is not null || sawData || (type switch { 0 => size != 2, 2 => size != 6, 3 => palette is null || size == 0 || size > palette.Length / 3, _ => true })) throw Invalid("PNG 透明度块无效。");
                transparency = body.ToArray();
            }
            else if (tag.SequenceEqual("IDAT"u8))
            {
                if (endedData || type == 3 && palette is null) throw Invalid("PNG 图像块顺序无效。");
                sawData = true; data.Write(body);
            }
            else if (tag.SequenceEqual("IEND"u8))
            {
                if (size != 0 || !sawData || offset + 12 != span.Length) throw Invalid("PNG 结束块无效。");
                ended = true; break;
            }
            else if ((tag[0] & 32) == 0 || tag.SequenceEqual("acTL"u8) || tag.SequenceEqual("fcTL"u8) || tag.SequenceEqual("fdAT"u8)) throw Invalid("不支持此 PNG 扩展或动画纹理。");
            offset += size + 12;
        }
        if (!ended || data.Length < 6) throw Invalid("PNG 图像不完整。");
        var channels = type switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw Invalid("PNG 颜色无效。") };
        var bits = channels * depth; var bpp = Math.Max(1, (bits + 7) / 8); var passes = interlace == 0 ? new[] { (0, 0, 1, 1) } : new[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) };
        var expected = 0;
        foreach (var (x, y, dx, dy) in passes)
        { var pw = Count(width, x, dx); var ph = Count(height, y, dy); if (pw > 0 && ph > 0) expected = checked(expected + ph * (1 + (pw * bits + 7) / 8)); }
        var packed = new byte[expected]; data.Position = 0;
        try
        {
            using var zlib = new ZLibStream(data, CompressionMode.Decompress, true);
            zlib.ReadExactly(packed);
            if (zlib.ReadByte() != -1) throw Invalid("PNG 解压数据超过像素预算。");
        }
        catch (EndOfStreamException) { throw Invalid("PNG 解压数据不完整。"); }
        var rgba = new byte[checked(width * height * 4)]; var cursor = 0;
        foreach (var (x, y, dx, dy) in passes)
        {
            var pw = Count(width, x, dx); var ph = Count(height, y, dy); if (pw == 0 || ph == 0) continue;
            var stride = (pw * bits + 7) / 8; var previous = new byte[stride]; var row = new byte[stride];
            for (var py = 0; py < ph; py++)
            {
                var filter = packed[cursor++]; if (filter > 4) throw Invalid("PNG 行过滤器无效。");
                for (var i = 0; i < stride; i++)
                {
                    var a = i >= bpp ? row[i - bpp] : 0; var b = previous[i]; var c = i >= bpp ? previous[i - bpp] : 0;
                    row[i] = unchecked((byte)(packed[cursor++] + (filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) })));
                }
                for (var px = 0; px < pw; px++) Pixel(row, px, depth, type, palette, transparency, rgba.AsSpan(((y + py * dy) * width + x + px * dx) * 4, 4));
                (row, previous) = (previous, row);
            }
        }
        return new(png, width, height, rgba);
    }
    private static int Count(int size, int start, int step) => size <= start ? 0 : (size - start + step - 1) / step;
    private static bool ValidDepth(int type, int depth) => type switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
    private static int Sample(byte[] row, int index, int depth) => depth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(index * 2, 2)) : depth == 8 ? row[index] : row[index * depth / 8] >> (8 - depth - index * depth % 8) & ((1 << depth) - 1);
    private static byte Scale(int value, int depth) => (byte)(depth == 16 ? value >> 8 : value * 255 / ((1 << depth) - 1));
    private static void Pixel(byte[] row, int x, int depth, int type, byte[]? palette, byte[]? trns, Span<byte> target)
    {
        target[3] = 255;
        if (type == 3)
        {
            var index = Sample(row, x, depth); if (palette is null || index >= palette.Length / 3) throw Invalid("PNG 调色板索引越界。");
            palette.AsSpan(index * 3, 3).CopyTo(target); if (trns is not null && index < trns.Length) target[3] = trns[index]; return;
        }
        var channels = type switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 }; var offset = x * channels;
        var r = Sample(row, offset, depth); target[0] = Scale(r, depth);
        if (type is 0 or 4)
        {
            target[1] = target[2] = target[0];
            if (type == 4) target[3] = Scale(Sample(row, offset + 1, depth), depth);
            else if (trns is not null && r == BinaryPrimitives.ReadUInt16BigEndian(trns)) target[3] = 0;
        }
        else
        {
            var g = Sample(row, offset + 1, depth); var b = Sample(row, offset + 2, depth); target[1] = Scale(g, depth); target[2] = Scale(b, depth);
            if (type == 6) target[3] = Scale(Sample(row, offset + 3, depth), depth);
            else if (trns is not null && r == BinaryPrimitives.ReadUInt16BigEndian(trns) && g == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4))) target[3] = 0;
        }
    }
    private static int Paeth(int a, int b, int c) { var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    { var crc = uint.MaxValue; foreach (var value in bytes) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : crc >> 1 ^ 0xedb88320u; } return ~crc; }
    private static InvalidDataException Invalid(string message) => new(message);
}
