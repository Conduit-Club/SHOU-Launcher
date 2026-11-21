using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record AssetObject(string LogicalName, string Hash, long Size)
{
    public string RelativePath => "objects/" + Hash[..2] + "/" + Hash;
    public Uri OfficialUri => new("https://resources.download.minecraft.net/" + Hash[..2] + "/" + Hash);
}
public sealed record AssetIndex(string Id, IReadOnlyList<AssetObject> Objects, bool Virtual, bool MapToResources);
public static class AssetResolver
{
    public static AssetIndex Parse(string id, string json)
    {
        SafePath.Identifier(id);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Asset index is not an object.");
        if (root["objects"] is not JsonObject objects) throw new InvalidDataException("Asset index has no objects.");
        var result = new List<AssetObject>();
        foreach (var entry in objects)
        {
            var hash = (string?)entry.Value?["hash"] ?? "";
            var size = (long?)entry.Value?["size"] ?? -1;
            if (hash.Length != 40 || hash.Any(c => !Uri.IsHexDigit(c)) || size < 0) throw new InvalidDataException("Asset object hash/size invalid.");
            result.Add(new(entry.Key, hash.ToLowerInvariant(), size));
        }
        return new(id, result, (bool?)root["virtual"] ?? false, (bool?)root["map_to_resources"] ?? false);
    }
    public static void MaterializeLegacyLayout(AssetIndex index, string assetsRoot, string gameDirectory)
    {
        if (!index.Virtual && !index.MapToResources) return;
        foreach (var asset in index.Objects)
        {
            var source = SafePath.Within(assetsRoot, asset.RelativePath);
            foreach (var root in new[] { index.Virtual ? Path.Combine(assetsRoot, "virtual", index.Id) : null,
                         index.MapToResources ? Path.Combine(gameDirectory, "resources") : null }.Where(p => p is not null))
            {
                var target = SafePath.Within(root!, asset.LogicalName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
        }
    }
}
