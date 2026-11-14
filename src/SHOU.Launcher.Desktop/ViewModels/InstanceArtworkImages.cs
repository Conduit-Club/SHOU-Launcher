using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SHOU.Launcher.Desktop.ViewModels;

internal static class InstanceArtworkImages
{
    private static readonly Dictionary<string, Bitmap> cache = new();
    internal static IImage For(string? key)
    {
        var name = key switch { "fabric" or "legacyfabric" => "Fabric", "forge" => "Anvil", "neoforge" => "NeoForge", "quilt" => "Quilt", "cleanroom" => "Cleanroom", "optifine" or "grasspath" => "GrassPath", "labymod" => "LabyMod", "liteloader" => "Egg", "cobblestone" => "CobbleStone", "commandblock" => "CommandBlock", "redstonelamp" => "RedstoneLampOn", "goldblock" => "GoldBlock", "redstoneblock" => "RedstoneBlock", _ => "Grass" };
        if (!cache.TryGetValue(name, out var image)) { using var stream = AssetLoader.Open(new("avares://SHOU.Launcher.Desktop/Assets/Minecraft/" + name + ".png")); cache[name] = image = new Bitmap(stream); }
        return image;
    }
}
