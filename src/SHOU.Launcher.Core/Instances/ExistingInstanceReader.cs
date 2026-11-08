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
        var result = await InspectDetailedAsync(selected, ct);
        if (result.Candidates.Count == 0 && result.Issues.Count > 0) throw new InvalidDataException(result.Issues[0].Reason);
        return result.Candidates;
    }
    public static async Task<ExistingInstanceInspection> InspectDetailedAsync(string selected, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(selected)) throw new ArgumentException("请选择完整的实例或 Minecraft 目录路径。");
        selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected)); VerifiedDownloader.RejectLink(selected);
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("所选目录不存在。");
        var versions = Directory.Exists(Path.Combine(selected, "versions")) ? Path.Combine(selected, "versions") : selected;
        VerifiedDownloader.RejectLink(versions);
        var ownJson = Literal(selected, Path.GetFileName(selected) + ".json");
        var folders = File.Exists(ownJson) ? new[] { selected } : Directory.EnumerateDirectories(versions).Take(1001).ToArray();
        if (folders.Length > 1000) throw new IOException("实例目录超过 1000 个，请选择具体实例。");
        List<ExistingInstanceCandidate> candidates = []; List<ExistingInstanceIssue> issues = [];
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            try
            {
            VerifiedDownloader.RejectLink(folder);
            var jsonPath = Literal(folder, name + ".json"); if (!File.Exists(jsonPath)) continue;
            var metadata = await ReadAsync(jsonPath, ct);
            if (metadata["mainClass"] is null && metadata["inheritsFrom"] is null) continue;
            var parent = Directory.GetParent(folder)!;
            var root = parent.Name == "versions" ? parent.Parent!.FullName : folder;
            var digest = await HashAsync(jsonPath, ct);
            var origin = new ExternalInstanceOrigin(root, folder, name, digest);
            var localIdentity = "local-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder) + "\0" + digest)))[..24];
            var resolved = await ResolveAsync(origin, localIdentity, "local", ct);
            metadata = resolved.Metadata;
            var game = IO.SafePath.Identifier((string?)metadata["clientVersion"] ?? resolved.InheritanceChain[0]);
            var libraryNames = (metadata["libraries"] as JsonArray ?? []).Select(n => (string?)n?["name"] ?? "").ToArray();
            var coordinates = new[] { ("legacyfabric", "net.legacyfabric:fabric-loader:"), ("fabric", "net.fabricmc:fabric-loader:"), ("quilt", "org.quiltmc:quilt-loader:"), ("neoforge", "net.neoforged:neoforge:"), ("cleanroom", "com.cleanroommc:cleanroom:"), ("forge", "net.minecraftforge:forge:"), ("liteloader", "com.mumfrey:liteloader:"), ("labymod", "net.labymod:labymod:"), ("optifine", "optifine:OptiFine:") };
            var detected = coordinates.FirstOrDefault(p => libraryNames.Any(n => n.StartsWith(p.Item2, StringComparison.Ordinal)));
            var loader = detected.Item1 ?? "vanilla"; var prefix = detected.Item2 ?? "\0";
            var loaderVersion = libraryNames.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
            loaderVersion = loaderVersion?.Split(':')[0];
            // Modern Forge profiles put the loader identity in public FML arguments instead of a forge library.
            if (loader == "vanilla" && (string?)metadata["mainClass"] is "cpw.mods.bootstraplauncher.BootstrapLauncher" or "cpw.mods.modlauncher.Launcher" or "net.minecraftforge.bootstrap.ForgeBootstrap")
            {
                var arguments = metadata["arguments"]?["game"] as JsonArray ?? [];
                string? Flag(string key)
                {
                    var positions = Enumerable.Range(0, Math.Max(0, arguments.Count - 1)).Where(i => arguments[i] is JsonValue v && v.TryGetValue<string>(out var text) && text == key).ToArray();
                    if (positions.Length > 1) throw new InvalidDataException("FML 实例身份重复，请检查原版本信息。");
                    return positions.Length == 1 && arguments[positions[0] + 1] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
                }
                var neo = Flag("--fml.neoForgeVersion"); var forge = Flag("--fml.forgeVersion");
                if (neo is null && forge is null || neo is not null && forge is not null) throw new InvalidDataException("无法核实 FML 实例的加载器身份。");
                if (Flag("--fml.mcVersion") is { } declaredGame && declaredGame != game) throw new InvalidDataException("FML 实例的游戏版本不一致。");
                loader = neo is null ? "forge" : "neoforge"; loaderVersion = IO.SafePath.Identifier(neo ?? forge!);
            }
            if (loader is "forge" or "optifine" && loaderVersion?.StartsWith(game + "-", StringComparison.Ordinal) == true) loaderVersion = loaderVersion[(game.Length + 1)..];
            // Mod/config-bearing profiles are isolated; plain profiles use the selected Minecraft root.
            var isolated = loader != "vanilla" || new[] { "mods", "config", "saves", "options.txt" }.Any(n => Directory.Exists(Path.Combine(folder, n)) || File.Exists(Path.Combine(folder, n)));
            var fallback = loader != "vanilla" ? loader : (string?)metadata["type"] switch { "snapshot" => "commandblock", "old_alpha" or "old_beta" => "cobblestone", _ => "auto" };
            var presentation = await LegacyInstancePresentation.ReadAsync(folder, fallback, ct);
            var candidate = new ExistingInstanceCandidate(name.Length > 160 ? name[..160] : name, localIdentity, game, loader, loaderVersion, isolated ? folder : root, origin, presentation);
            candidates.Add(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
            { issues.Add(new(name, "实例元数据或继承链无法核实（" + ex.GetType().Name + "），原目录保持不变。")); }
        }
        return new(candidates, issues);
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
