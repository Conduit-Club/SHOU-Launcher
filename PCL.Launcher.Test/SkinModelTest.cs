using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Launcher.Test;

[TestClass]
public sealed class SkinModelTest
{
    [STATestMethod]
    public void ClassicSlimAndLegacyPlayersHaveCompleteBodies()
    {
        var modern = Texture(64, 64);
        var classic = MinecraftSkinModel.Create(new(modern, null, false), false, false);
        var slim = MinecraftSkinModel.Create(new(modern, null, true), false, false);
        var legacy = MinecraftSkinModel.Create(new(Texture(64, 32), null, false), true, false);
        Assert.AreEqual(32d, classic.Bounds.SizeY, 0.01);
        Assert.AreEqual(32d, slim.Bounds.SizeY, 0.01);
        Assert.AreEqual(32d, legacy.Bounds.SizeY, 0.01);
        Assert.IsTrue(slim.Bounds.SizeX < classic.Bounds.SizeX, "Slim arms must be narrower.");
        Assert.AreEqual(classic.Bounds.SizeX, legacy.Bounds.SizeX, 0.01);
        Assert.IsTrue(classic.IsFrozen);
    }

    [STATestMethod]
    public void OuterLayerAndCapeCanBeShownIndependently()
    {
        var data = new SkinPreviewData(Texture(64, 64), Texture(64, 32), false);
        var bare = MinecraftSkinModel.Create(data, false, false);
        var clothed = MinecraftSkinModel.Create(data, true, false);
        var caped = MinecraftSkinModel.Create(data, false, true);
        Assert.IsTrue(clothed.Bounds.SizeY > bare.Bounds.SizeY);
        Assert.IsTrue(caped.Bounds.Z < bare.Bounds.Z);
        Assert.AreEqual(6, ((Model3DGroup)clothed.Children[^1]).Children.Count);
    }

    [STATestMethod]
    public void HighResolutionSkinsWorkAndInvalidDimensionsAreRejected()
    {
        var model = MinecraftSkinModel.Create(new(Texture(128, 128), null, true), true, false);
        Assert.IsTrue(model.Bounds.SizeY > 32);
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => MinecraftSkinModel.Create(new(Texture(64, 48), null, false), false, false));
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => MicrosoftSkinPreview.ValidateSize(Texture(64, 64), true));
    }

    internal static BitmapSource Texture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 180; pixels[i + 1] = 140; pixels[i + 2] = 100; pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }
}
