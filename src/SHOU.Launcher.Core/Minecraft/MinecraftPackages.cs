using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Minecraft;

public static class MinecraftPackages
{
    public static IReadOnlyList<(MinecraftPackage File, Uri Source)> Read(string version, JsonObject metadata)
    {
        IO.SafePath.Identifier(version);
        var result = new List<(MinecraftPackage, Uri)>();
        foreach (var kind in new[] { "client", "server" })
        {
            if (metadata["downloads"]?[kind] is not JsonObject entry) continue;
            var sha1 = (string?)entry["sha1"];
            var size = (long?)entry["size"];
            if (sha1 is null || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit) || size is null or <= 0 or > 2147483648
                || !Uri.TryCreate((string?)entry["url"], UriKind.Absolute, out var uri) || !Trusted(uri))
                throw new InvalidDataException("Minecraft 文件来源或校验信息无效。");
            result.Add((new(kind, $"minecraft-{version}-{kind}.jar", size.Value, sha1.ToLowerInvariant()), uri));
        }
        return result;
    }
    public static bool Trusted(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host is "piston-data.mojang.com" or "launcher.mojang.com" or "resources.download.minecraft.net";
}
