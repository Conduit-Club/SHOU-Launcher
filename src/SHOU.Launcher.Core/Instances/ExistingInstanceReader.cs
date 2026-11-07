using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Reads only the selected version's public metadata. Never imports account caches, launcher config or credentials.</summary>
public static class ExistingInstanceReader
{
    private static string Literal(string root, string name)
    {
        if (name.Length is 0 or > 180 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl)) throw new InvalidDataException("实例版本目录名称无效。");
        var path = Path.Combine(root, name); VerifiedDownloader.RejectLink(path); return path;
    }
    public static async Task<IReadOnlyList<ExistingInstanceCandidate>> InspectAsync(string selected, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(selected)) throw new ArgumentException("请选择完整的实例或 Minecraft 目录路径。");
        selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected)); VerifiedDownloader.RejectLink(selected);
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("所选目录不存在。");
        var versions = Directory.Exists(Path.Combine(selected, "versions")) ? Path.Combine(selected, "versions") : selected;
        VerifiedDownloader.RejectLink(versions);
        var ownJson = Literal(selected, Path.GetFileName(selected) + ".json");
        var folders = File.Exists(ownJson) ? new[] { selected } : Directory.EnumerateDirectories(versions).Take(1001).ToArray();
        if (folders.Length > 1000) throw new IOException("实例目录超过 1000 个，请选择具体实例。");
        List<ExistingInstanceCandidate> candidates = [];
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(folder);
            var name = Path.GetFileName(folder); var jsonPath = Literal(folder, name + ".json");
            if (!File.Exists(jsonPath)) continue;
            var metadata = await ReadAsync(jsonPath, ct);
            if (metadata["mainClass"] is null && metadata["inheritsFrom"] is null) continue;
            var parent = Directory.GetParent(folder)!;
            var root = parent.Name == "versions" ? parent.Parent!.FullName : Directory.GetParent(versions)?.FullName ?? versions;
            var game = (string?)metadata["clientVersion"] ?? (string?)metadata["inheritsFrom"] ?? (string?)metadata["id"] ?? name;
            var libraryNames = (metadata["libraries"] as JsonArray ?? []).Select(n => (string?)n?["name"] ?? "").ToArray();
            var loader = libraryNames.Any(n => n.StartsWith("net.fabricmc:fabric-loader:", StringComparison.Ordinal)) ? "fabric"
                : libraryNames.Any(n => n.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)) ? "quilt"
                : libraryNames.Any(n => n.StartsWith("net.neoforged:neoforge:", StringComparison.Ordinal)) ? "neoforge"
                : libraryNames.Any(n => n.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal)) ? "forge" : "vanilla";
            var prefix = loader switch { "fabric" => "net.fabricmc:fabric-loader:", "quilt" => "org.quiltmc:quilt-loader:", "forge" => "net.minecraftforge:forge:", "neoforge" => "net.neoforged:neoforge:", _ => "\0" };
            var loaderVersion = libraryNames.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
            var digest = await HashAsync(jsonPath, ct);
            var origin = new ExternalInstanceOrigin(root, folder, name, digest);
            // Mod/config-bearing profiles are isolated; plain profiles use the selected Minecraft root.
            var isolated = new[] { "mods", "config", "saves", "options.txt" }.Any(n => Directory.Exists(Path.Combine(folder, n)) || File.Exists(Path.Combine(folder, n)));
            var candidate = new ExistingInstanceCandidate(name, "local-" + digest[..24], game, loader, loaderVersion, isolated ? folder : root, origin);
            _ = await ResolveAsync(candidate.Origin, candidate.VersionId, candidate.GameVersion, ct);
            candidates.Add(candidate);
        }
        return candidates;
    }
    public static async Task<ResolvedVersion> ResolveAsync(ExternalInstanceOrigin origin, string versionId, string gameVersion, CancellationToken ct)
    {
        VerifiedDownloader.RejectLink(origin.MinecraftRoot); VerifiedDownloader.RejectLink(origin.VersionDirectory);
        var original = Literal(origin.VersionDirectory, origin.OriginalVersionId + ".json");
        if (await HashAsync(original, ct) != origin.MetadataSha256) throw new IOException("原实例版本信息已变化，请重新接入目录；原文件保持。");
        var seen = new HashSet<string>(StringComparer.Ordinal); var chain = new List<string>();
        async Task<JsonObject> Visit(string id, string folder)
        {
            if (!seen.Add(id) || seen.Count > 24) throw new InvalidDataException("本地实例继承链循环或过长。");
            var metadata = await ReadAsync(Literal(folder, id + ".json"), ct);
            if ((string?)metadata["inheritsFrom"] is { } parent)
                metadata = VersionMetadataResolver.Merge(await Visit(parent, Literal(Path.Combine(origin.MinecraftRoot, "versions"), parent)), metadata);
            chain.Add(id); return metadata;
        }
        var merged = await Visit(origin.OriginalVersionId, origin.VersionDirectory);
        merged["id"] = versionId; merged.Remove("inheritsFrom"); merged["jar"] = versionId;
        if (merged["mainClass"] is null || merged["libraries"] is not JsonArray) throw new InvalidDataException("所选目录缺少有效的游戏版本信息。");
        return new(versionId, merged, original, chain.Append(gameVersion).Append(versionId).Distinct().ToArray());
    }
    private static async Task<JsonObject> ReadAsync(string path, CancellationToken ct)
    {
        VerifiedDownloader.RejectLink(path); var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 2 * 1024 * 1024) throw new InvalidDataException("实例版本信息不存在或超过 2 MiB。");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? throw new InvalidDataException("实例版本信息无效。");
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { VerifiedDownloader.RejectLink(path); var info = new FileInfo(path); if (!info.Exists || info.Length > 2 * 1024 * 1024) throw new InvalidDataException("实例版本信息无效。"); await using var input = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)); }
}
