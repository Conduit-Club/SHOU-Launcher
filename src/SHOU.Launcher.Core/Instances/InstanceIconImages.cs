using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Tools;

namespace SHOU.Launcher.Core.Instances;

public sealed record InstanceIconPixels(int Width, int Height, ReadOnlyMemory<byte> Rgba);
public static class InstanceIconImages
{
    public static readonly string[] Builtins = ["auto", "minecraft", "fabric", "quilt", "forge", "neoforge", "cleanroom", "legacyfabric", "optifine", "labymod", "liteloader", "grasspath", "cobblestone", "commandblock", "redstonelamp", "goldblock", "redstoneblock"];
    public static byte[] Crop(InstanceIconPixels source, double zoom = 1, double x = .5, double y = .5, CancellationToken ct = default)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(x) || !double.IsFinite(y) || zoom is < 1 or > 8 || x is < 0 or > 1 || y is < 0 or > 1 || source.Width is < 1 or > 2048 || source.Height is < 1 or > 2048 || source.Rgba.Length != source.Width * source.Height * 4) throw new ArgumentException("裁剪区域或图像大小无效。");
        var side = Math.Min(source.Width, source.Height) / zoom; var left = (source.Width - side) * x; var top = (source.Height - side) * y;
        var pixels = source.Rgba.Span; var output = new byte[256 * 256 * 4]; Span<double> rgb = stackalloc double[3];
        for (var py = 0; py < 256; py++)
        {
            ct.ThrowIfCancellationRequested();
            for (var px = 0; px < 256; px++)
            {
                var sx = Math.Clamp(left + (px + .5) * side / 256 - .5, 0, source.Width - 1); var sy = Math.Clamp(top + (py + .5) * side / 256 - .5, 0, source.Height - 1);
                var ix = (int)sx; var iy = (int)sy; var alpha = 0d; rgb.Clear();
                for (var dy = 0; dy < 2; dy++) for (var dx = 0; dx < 2; dx++)
                {
                    var weight = (dx == 0 ? 1 - (sx - ix) : sx - ix) * (dy == 0 ? 1 - (sy - iy) : sy - iy);
                    var i = (Math.Min(iy + dy, source.Height - 1) * source.Width + Math.Min(ix + dx, source.Width - 1)) * 4;
                    var a = pixels[i + 3] * weight; alpha += a; for (var c = 0; c < 3; c++) rgb[c] += pixels[i + c] * a;
                }
                var o = (py * 256 + px) * 4; output[o + 3] = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
                for (var c = 0; c < 3; c++) output[o + c] = alpha <= 0 ? (byte)0 : (byte)Math.Clamp(Math.Round(rgb[c] / alpha), 0, 255);
            }
        }
        return ToolImages.EncodeRgba(256, 256, output);
    }
    public static byte[] Normalize(ReadOnlyMemory<byte> png, CancellationToken ct = default)
    { var image = AccountAppearancePng.DecodeIcon(png); return Crop(new(image.Width, image.Height, image.Rgba), ct: ct); }
}
