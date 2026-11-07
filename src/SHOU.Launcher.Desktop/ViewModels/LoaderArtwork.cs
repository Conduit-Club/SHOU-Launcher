using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SHOU.Launcher.Desktop.ViewModels;

internal static class LoaderArtwork
{
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);
    public static IImage For(string loader)
    {
        var name = loader switch { "fabric" or "legacyfabric" => "Fabric", "quilt" => "Quilt", "optifine" => "Grass", "cleanroom" => "Cleanroom", "labymod" => "LabyMod", "liteloader" => "Egg", _ => "Anvil" };
        if (!Images.TryGetValue(name, out var image))
        {
            using var stream = AssetLoader.Open(new("avares://SHOU.Launcher.Desktop/Assets/Minecraft/" + name + ".png"));
            Images[name] = image = new Bitmap(stream);
        }
        return image;
    }
}
