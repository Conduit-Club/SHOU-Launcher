using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

internal static class LegacyInstancePresentation
{
    internal static async Task<InstancePresentation> ReadAsync(string folder, string fallbackIcon, CancellationToken ct)
    {
        var path = SafePath.Within(folder, "PCL/Setup.ini");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            var bytes = await WorldContentArchive.ReadFileAsync(folder, path, 256 * 1024, ct);
            var text = bytes.AsSpan().StartsWith(new byte[] { 255, 254 }) ? Encoding.Unicode.GetString(bytes.AsSpan(2)) : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            foreach (var line in text.Split('\n'))
            {
                var colon = line.IndexOf(':'); var equals = line.IndexOf('=');
                var i = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals); if (i < 1) continue;
                var key = line[..i].Trim(); if (key is "Logo" or "IsStar" or "CustomInfo" or "DisplayType" or "VersionLaunchCount") values[key] = line[(i + 1)..].Trim();
            }
        }
        var customFile = SafePath.Within(folder, "PCL/Logo.png");
        var name = Path.GetFileName((values.GetValueOrDefault("Logo") ?? "").Replace('\\', '/')).ToLowerInvariant();
        var icon = File.Exists(customFile) ? "auto" : name switch
        {
            "grass.png" => "minecraft", "fabric.png" => "fabric", "anvil.png" => "forge", "neoforge.png" => "neoforge", "quilt.png" => "quilt", "cleanroom.png" => "cleanroom", "labymod.png" => "labymod", "egg.png" => "liteloader", "grasspath.png" => "grasspath", "cobblestone.png" => "cobblestone", "commandblock.png" => "commandblock", "redstonelampon.png" => "redstonelamp", "goldblock.png" => "goldblock", "redstoneblock.png" => "redstoneblock", _ => fallbackIcon
        };
        var category = values.GetValueOrDefault("DisplayType") switch { "1" => "隐藏", "2" => "可安装模组", "3" => "原版", "4" => "远古版", "5" => "愚人节版", "6" => "需检查", _ => "auto" };
        var description = values.GetValueOrDefault("CustomInfo") ?? "";
        if (description.Length > 4096 || description.Any(char.IsControl)) description = "";
        var favorite = values.GetValueOrDefault("DisplayType") == "-1" || values.GetValueOrDefault("IsStar") is { } star && (star.Equals("true", StringComparison.OrdinalIgnoreCase) || star == "1");
        var count = long.TryParse(values.GetValueOrDefault("VersionLaunchCount"), out var n) && n >= 0 ? n : 0;
        return new(icon, category, description, favorite, count);
    }
}
