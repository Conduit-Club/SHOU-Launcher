using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PCL;

/// <summary>A textured Minecraft player, including classic, slim, legacy and outer-layer UV layouts.</summary>
public static class MinecraftSkinModel
{
    private readonly record struct AtlasBox(int X, int Y, int Width, int Height, int Depth);

    public static Model3DGroup Create(SkinPreviewData data, bool showOuterLayer, bool showCape)
    {
        MicrosoftSkinPreview.ValidateSize(data.Skin, isCape: false);
        var model = new Model3DGroup();
        var outer = new Model3DGroup();
        var legacy = data.Skin.PixelHeight * 2 == data.Skin.PixelWidth;
        var armWidth = data.IsSlim && !legacy ? 3 : 4;

        if (showCape && data.Cape is { } cape)
        {
            MicrosoftSkinPreview.ValidateSize(cape, isCape: true);
            var capeModel = new Model3DGroup
            {
                Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 12),
                    new Point3D(0, 8, -3))
            };
            AddBox(capeModel, cape, new(0, 0, 10, 16, 1), new(0, 0, -3), 0,
                forceOpaque: false, swapFrontBack: true);
            model.Children.Add(capeModel);
        }

        AddPart(new(0, 0, 8, 8, 8), new(0, 12, 0), new(32, 0, 8, 8, 8), 0.5);
        AddPart(new(16, 16, 8, 12, 4), new(0, 2, 0), new(16, 32, 8, 12, 4));
        AddPart(new(40, 16, armWidth, 12, 4), new(-4 - armWidth / 2d, 2, 0),
            new(40, 32, armWidth, 12, 4), shoulderAngle: -4);
        AddPart(legacy ? new(40, 16, 4, 12, 4) : new(32, 48, armWidth, 12, 4),
            new(4 + armWidth / 2d, 2, 0), new(48, 48, armWidth, 12, 4),
            mirror: legacy, shoulderAngle: 4);
        AddPart(new(0, 16, 4, 12, 4), new(-2, -10, 0), new(0, 32, 4, 12, 4));
        AddPart(legacy ? new(0, 16, 4, 12, 4) : new(16, 48, 4, 12, 4),
            new(2, -10, 0), new(0, 48, 4, 12, 4), mirror: legacy);
        // Draw opaque parts before translucent clothing.
        model.Children.Add(outer);
        model.Freeze();
        return model;

        void AddPart(AtlasBox uv, Point3D center, AtlasBox overlay, double inflate = 0.25,
            bool mirror = false, double shoulderAngle = 0)
        {
            var part = new Model3DGroup();
            var clothing = new Model3DGroup();
            if (shoulderAngle != 0)
            {
                var rotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), shoulderAngle),
                    new Point3D(center.X, 8, 0));
                part.Transform = rotation;
                clothing.Transform = rotation;
            }
            AddBox(part, data.Skin, uv, center, 0, mirror: mirror);
            model.Children.Add(part);
            if (!showOuterLayer || (legacy && overlay.Y != 0)) return;
            // Legacy skins with no alpha channel use the spare area as opaque padding, not a hat.
            if (legacy && !HasTransparency(data.Skin, new Int32Rect(32, 0, 32, 16))) return;
            AddBox(clothing, data.Skin, overlay, center, inflate, forceOpaque: false);
            outer.Children.Add(clothing);
        }
    }

    private static void AddBox(Model3DGroup group, BitmapSource texture, AtlasBox uv, Point3D center,
        double inflate, bool forceOpaque = true, bool mirror = false, bool swapFrontBack = false)
    {
        var x0 = center.X - uv.Width / 2d - inflate;
        var x1 = center.X + uv.Width / 2d + inflate;
        var y0 = center.Y - uv.Height / 2d - inflate;
        var y1 = center.Y + uv.Height / 2d + inflate;
        var z0 = center.Z - uv.Depth / 2d - inflate;
        var z1 = center.Z + uv.Depth / 2d + inflate;
        var top = new Int32Rect(uv.X + uv.Depth, uv.Y, uv.Width, uv.Depth);
        var bottom = new Int32Rect(uv.X + uv.Depth + uv.Width, uv.Y, uv.Width, uv.Depth);
        var right = new Int32Rect(uv.X, uv.Y + uv.Depth, uv.Depth, uv.Height);
        var front = new Int32Rect(uv.X + uv.Depth, uv.Y + uv.Depth, uv.Width, uv.Height);
        var left = new Int32Rect(uv.X + uv.Depth + uv.Width, uv.Y + uv.Depth, uv.Depth, uv.Height);
        var back = new Int32Rect(uv.X + 2 * uv.Depth + uv.Width, uv.Y + uv.Depth, uv.Width, uv.Height);
        if (mirror) (right, left) = (left, right);
        if (swapFrontBack) (front, back) = (back, front);

        Face(front, new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1));
        Face(back, new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0));
        Face(right, new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0));
        Face(left, new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1));
        Face(top, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1));
        Face(bottom, new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0));

        void Face(Int32Rect region, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            var image = PixelTexture(texture, region, forceOpaque);
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill, TileMode = TileMode.None };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            var material = new DiffuseMaterial(brush);
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { a, b, c, d },
                TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 },
                TextureCoordinates = mirror
                    ? new PointCollection { new(1, 0), new(0, 0), new(0, 1), new(1, 1) }
                    : new PointCollection { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }
            };
            group.Children.Add(new GeometryModel3D(mesh, material));
        }
    }

    private static BitmapSource PixelTexture(BitmapSource source, Int32Rect atlasRegion, bool forceOpaque)
    {
        var scale = source.PixelWidth / 64;
        var region = new Int32Rect(atlasRegion.X * scale, atlasRegion.Y * scale,
            atlasRegion.Width * scale, atlasRegion.Height * scale);
        var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[region.Width * region.Height * 4];
        rgba.CopyPixels(region, pixels, region.Width * 4, 0);
        // Upscale each texel ourselves; WPF's 3D texture sampler otherwise blurs pixel art.
        var factor = Math.Max(1, 128 / Math.Max(region.Width, region.Height));
        var width = region.Width * factor;
        var height = region.Height * factor;
        var enlarged = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var from = ((y / factor) * region.Width + x / factor) * 4;
            var to = (y * width + x) * 4;
            enlarged[to] = pixels[from];
            enlarged[to + 1] = pixels[from + 1];
            enlarged[to + 2] = pixels[from + 2];
            enlarged[to + 3] = forceOpaque ? (byte)255 : pixels[from + 3];
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, enlarged, width * 4);
        result.Freeze();
        return result;
    }

    private static bool HasTransparency(BitmapSource source, Int32Rect region)
    {
        var image = PixelTexture(source, region, forceOpaque: false);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 128) return true;
        return false;
    }
}
