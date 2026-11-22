using Avalonia.Media.Imaging;
using BitMiracle.LibTiff.Classic;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;
using SkiaSharp;

namespace SHOU.Launcher.Desktop.ViewModels;

internal static class ScreenshotImages
{
    private static bool ValidSize(int width, int height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= 64 * 1024 * 1024;
    public static Bitmap? Read(InstanceContentEntry file, bool thumbnail, CancellationToken ct = default)
    {
        try
        {
            SafePath.RejectLinks(file.FullPath); var info = new FileInfo(file.FullPath);
            if (!info.Exists || info.Length is <= 0 or > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var codec = SKCodec.Create(new SKManagedStream(stream, false));
            ct.ThrowIfCancellationRequested();
            if (codec is null) { stream.Position = 0; return ReadTiff(stream, thumbnail, ct); }
            var size = codec.Info; if (!ValidSize(size.Width, size.Height)) return null;
            stream.Position = 0;
            return !thumbnail ? new Bitmap(stream) : (long)size.Width * 270 >= (long)size.Height * 480 ? Bitmap.DecodeToWidth(stream, 480) : Bitmap.DecodeToHeight(stream, 270);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException) { return null; }
    }
    private static Bitmap? ReadTiff(Stream stream, bool thumbnail, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[4]; if (stream.Read(header) != 4 || !(header.SequenceEqual(new byte[] { 73, 73, 42, 0 }) || header.SequenceEqual(new byte[] { 77, 77, 0, 42 }))) return null;
        stream.Position = 0; using var tiff = Tiff.ClientOpen("screenshot", "r", stream, new TiffStream());
        if (tiff is null) return null;
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0; var height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        if (!ValidSize(width, height)) return null; ct.ThrowIfCancellationRequested();
        var raster = new int[checked(width * height)]; if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT, true)) return null;
        ct.ThrowIfCancellationRequested(); var scale = thumbnail ? Math.Min(1d, Math.Min(480d / width, 270d / height)) : 1d;
        var w = Math.Max(1, (int)(width * scale)); var h = Math.Max(1, (int)(height * scale));
        using var image = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul); var pixels = new byte[checked(w * h * 4)];
        for (var y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < w; x++)
            { var value = raster[Math.Min(height - 1, (int)(y / scale)) * width + Math.Min(width - 1, (int)(x / scale))]; var i = (y * w + x) * 4; pixels[i] = (byte)Tiff.GetR(value); pixels[i + 1] = (byte)Tiff.GetG(value); pixels[i + 2] = (byte)Tiff.GetB(value); pixels[i + 3] = (byte)Tiff.GetA(value); }
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, image.GetPixels(), pixels.Length);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); using var output = encoded.AsStream(); return new Bitmap(output);
    }
}
