using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Content;

public sealed partial class LocalPackArchive
{
    private static async Task<LocalPackDescriptor?> ReadLegacyAsync(PackArchiveReader archive, IReadOnlyDictionary<string, ZipArchiveEntry> entries, string sha, CancellationToken ct)
    {
        static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
        static string String(JsonElement node, string key, string fallback = "") => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        async Task<JsonDocument> Json(ZipArchiveEntry entry) => PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(entry, int.MaxValue, ct).ConfigureAwait(false));
        // Preserve WPF's format precedence: a real root marker wins over metadata in a child folder.
        var names = new[] { "shou-instance.json", "mcbbs.packmeta", "mmc-pack.json", "modrinth.index.json", "manifest.json", "modpack.json" };
        var scope = names.Any(entries.ContainsKey) ? "" : entries.Keys.FirstOrDefault(p => p.Count(c => c == '/') == 1 && names.Contains(Leaf(p), StringComparer.Ordinal)) is { } wrapped ? wrapped[..(wrapped.IndexOf('/') + 1)] : "";
        var marker = names.Select(n => entries.GetValueOrDefault(scope + n)).FirstOrDefault(e => e is not null);
        if (marker is not null && Leaf(marker.FullName) is "modrinth.index.json" or "shou-instance.json") return null;
        if (marker is not null && Leaf(marker.FullName) == "manifest.json")
        { using var manifest = await Json(marker); if (!manifest.RootElement.TryGetProperty("addons", out _)) return null; }
        LocalPackFormat format; string prefix, payload, name, game; string? jvm = null, args = null; int? memory = null; string? clientJar = null; var complete = false; JsonObject? patch = null; var components = new List<LegacyPackComponent>();
        if (marker is not null)
        {
            prefix = marker.FullName[..^Leaf(marker.FullName).Length]; using var data = await Json(marker); var root = data.RootElement;
            name = String(root, "name", "导入的整合包"); game = "";
            switch (Leaf(marker.FullName))
            {
                case "modpack.json":
                    format = LocalPackFormat.Hmcl; game = String(root, "gameVersion"); payload = prefix + "minecraft/"; break;
                case "mcbbs.packmeta": case "manifest.json":
                    format = LocalPackFormat.Mcbbs; payload = prefix + "overrides/";
                    foreach (var addon in root.GetProperty("addons").EnumerateArray()) { var id = String(addon, "id").ToLowerInvariant(); var version = String(addon, "version"); if (id is "game" or "minecraft") game = version; else if (Loader(id) is { } loader) components.Add(new(loader, version)); }
                    if (root.TryGetProperty("launchInfo", out var launch)) { jvm = Arguments(launch, "javaArgument"); args = Arguments(launch, "launchArgument"); }
                    break;
                default:
                    format = LocalPackFormat.MultiMC; payload = prefix + ".minecraft/";
                    if (!entries.Keys.Any(p => p.StartsWith(payload, StringComparison.Ordinal))) payload = prefix + "minecraft/";
                    var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (entries.TryGetValue(prefix + "instance.cfg", out var config))
                    { using var text = new StreamReader(config.Open()); foreach (var line in (await text.ReadToEndAsync(ct)).Split('\n')) { var index = line.IndexOf('='); if (index > 0) cfg[line[..index].Trim()] = line[(index + 1)..].Trim(); } }
                    name = cfg.GetValueOrDefault("name", name); if (int.TryParse(cfg.GetValueOrDefault("MaxMemAlloc"), out var ram) && ram > 0) memory = ram;
                    if (cfg.GetValueOrDefault("OverrideJavaArgs")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) jvm = cfg.GetValueOrDefault("JvmArgs");
                    var ids = new HashSet<string>(StringComparer.Ordinal); foreach (var component in root.GetProperty("components").EnumerateArray())
                    { var id = String(component, "uid"); var version = String(component, "version"); ids.Add(id); if (id == "net.minecraft") game = version; else if (Loader(id) is { } loader) components.Add(new(loader == "forge" && version.StartsWith("0.", StringComparison.Ordinal) ? "cleanroom" : loader, version)); }
                    var patches = new List<JsonObject>();
                    foreach (var file in entries.Values.Where(e => e.FullName.StartsWith(prefix + "patches/", StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    { using var json = await Json(file); var node = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject(); if (ids.Contains((string?)node["uid"] ?? ""))
                        {
                            patches.Add(node); var loader = Loader((string?)node["uid"] ?? "");
                            for (var i = 0; i < components.Count; i++) if (components[i].Loader == loader) components[i] = components[i] with { Overridden = true };
                        } }
                    foreach (var item in patches.OrderBy(n => (int?)n["order"] ?? 0)) patch = MergePatch(patch ?? new(), item);
                    break;
            }
        }
        else
        {
            // Restore the original ordinary ZIP layout: .../versions/<id>/<id>.json.
            var version = entries.Values.FirstOrDefault(e => { var p = e.FullName.Split('/'); return p.Length >= 3 && p[^3] == "versions" && p[^1] == p[^2] + ".json"; });
            if (version is null) return null;
            var offset = version.FullName.LastIndexOf("versions/", StringComparison.Ordinal); prefix = version.FullName[..offset]; payload = prefix; name = version.FullName[(offset + 9)..].Split('/')[0]; format = LocalPackFormat.CompressedInstance;
            using var json = await Json(version); patch = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
            game = String(json.RootElement, "inheritsFrom", String(json.RootElement, "id", name)); complete = !json.RootElement.TryGetProperty("inheritsFrom", out _);
            var jarId = (string?)patch["jar"] ?? name;
            if (entries.ContainsKey(prefix + $"versions/{jarId}/{jarId}.jar")) clientJar = $"versions/{jarId}/{jarId}.jar";
            // Walk the archive's inheritance chain, retaining all public version metadata.
            var seen = new HashSet<string>(StringComparer.Ordinal) { name };
            while (seen.Add(game) && entries.TryGetValue(prefix + "versions/" + game + "/" + game + ".json", out var parent))
            { using var parentJson = await Json(parent);
                patch = Minecraft.VersionMetadataResolver.Merge(JsonNode.Parse(parentJson.RootElement.GetRawText())!.AsObject(), patch);
                if (clientJar is null && entries.ContainsKey(prefix + $"versions/{game}/{game}.jar")) clientJar = $"versions/{game}/{game}.jar";
                var next = String(parentJson.RootElement, "inheritsFrom"); if (next.Length == 0) { complete = true; break; } game = next; }
            foreach (var component in InferComponents(patch)) components.Add(component);
        }
        if (string.IsNullOrWhiteSpace(game)) throw new InvalidDataException("整合包缺少 Minecraft 基础版本。"); IO.SafePath.Identifier(game);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values.Where(e => e.FullName.StartsWith(payload, StringComparison.Ordinal))) { var relative = entry.FullName[payload.Length..]; if (relative.Length == 0) continue; PackPayloadPath.Validate(relative); mappings[relative] = entry.FullName; }
        if (format == LocalPackFormat.MultiMC)
            foreach (var entry in entries.Values.Where(e => e.FullName.StartsWith(prefix + "libraries/", StringComparison.Ordinal))) mappings["libraries/" + entry.FullName[(prefix.Length + 10)..]] = entry.FullName;
        // HMCL's minecraft layer may include its fully merged version profile.
        if (patch is null)
        {
            var profile = mappings.Where(p => p.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && (p.Key.Count(c => c == '/') == 0 || p.Key.StartsWith("versions/", StringComparison.Ordinal))).FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.Key) == name || Path.GetFileNameWithoutExtension(p.Key) == game);
            if (profile.Value is not null) { using var json = await Json(entries[profile.Value]); if (json.RootElement.TryGetProperty("mainClass", out _)) patch = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject(); }
        }
        if (components.Count == 0 && patch is not null) components.AddRange(InferComponents(patch));
        components = components.Distinct().ToList(); var primary = components.FirstOrDefault(c => c.Loader != "optifine") ?? components.FirstOrDefault();
        archive.Verify();
        return new(new(format, name, game, primary?.Loader ?? "vanilla", primary?.Version, sha, [], true, LocalFiles: mappings.Keys.ToArray(), RecommendedMemoryMiB: memory,
            LegacyProfile: new(patch?.ToJsonString(), components, jvm, args, clientJar, complete)), [], mappings, false);
    }
    private static string? Loader(string id) => id switch
    { "forge" or "net.minecraftforge" => "forge", "neoforge" or "net.neoforged" => "neoforge", "fabric" or "net.fabricmc.fabric-loader" => "fabric", "quilt" or "org.quiltmc.quilt-loader" => "quilt", "cleanroom" => "cleanroom", "legacyfabric" => "legacyfabric", "optifine" or "optifine.OptiFine" => "optifine", "liteloader" or "com.mumfrey.liteloader" => "liteloader", _ => null };
    private static string? Arguments(JsonElement node, string key) => !node.TryGetProperty(key, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Array ? string.Join(' ', value.EnumerateArray().Select(e => e.GetString())) : null;
    private static JsonObject MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (key is "uid" or "order" or "version") continue;
            if (key is "libraries" or "+libraries") { var list = target["libraries"] as JsonArray ?? new(); if (target["libraries"] is null) target["libraries"] = list; foreach (var library in value!.AsArray()) list.Add(library?.DeepClone()); }
            else if (key is "+jvmArgs" or "+gameArgs") { var arguments = target["arguments"] as JsonObject ?? new(); if (target["arguments"] is null) target["arguments"] = arguments; var kind = key == "+jvmArgs" ? "jvm" : "game"; var list = arguments[kind] as JsonArray ?? new(); if (arguments[kind] is null) arguments[kind] = list; foreach (var arg in value!.AsArray()) list.Add(arg?.DeepClone()); }
            else target[key] = value?.DeepClone();
        }
        return target;
    }
    private static IEnumerable<LegacyPackComponent> InferComponents(JsonObject metadata)
    {
        foreach (var node in metadata["libraries"] as JsonArray ?? [])
        {
            var name = (string?)node?["name"] ?? ""; var parts = name.Split(':'); if (parts.Length < 3) continue;
            var loader = parts[0] switch { "net.fabricmc" when parts[1] == "fabric-loader" => "fabric", "org.quiltmc" when parts[1] == "quilt-loader" => "quilt", "net.minecraftforge" when parts[1] == "forge" => "forge", "net.neoforged" when parts[1] == "neoforge" => "neoforge", "optifine" => "optifine", _ => null };
            if (loader is not null) { var version = parts[2]; if (loader == "forge" && version.IndexOf('-') is var dash && dash >= 0) version = version[(dash + 1)..]; yield return new(loader, version, true); }
        }
    }
    internal async Task<LocalPackInspection> StageLegacyAsync(string path, string expectedSha, string directory, CancellationToken ct, IProgress<PackInstallProgress>? progress)
    {
        using var archive = PackArchiveReader.Open(path); var actual = await archive.Sha256Async(ct); if (actual != expectedSha) throw new IOException("整合包自预检后已改变。");
        var descriptor = await ReadAsync(path, ct); if (descriptor.Inspection.LegacyProfile is null) throw new InvalidDataException("旧格式预检已失效。");
        IO.SafePath.RejectLinks(directory); Directory.CreateDirectory(directory);
        PackDiskSpace.Require(directory, descriptor.OverrideEntries.Values.Sum(p => archive.Zip.GetEntry(p)!.Length));
        var installed = new List<ContentInstalledFile>(); var count = 0;
        foreach (var (relative, source) in descriptor.OverrideEntries)
        {
            ct.ThrowIfCancellationRequested(); var file = await PackArchiveReader.StreamEntryAsync(archive.Zip.GetEntry(source)!, ContentInstallFiles.Literal(directory, relative), ct);
            installed.Add(new("legacy-local", actual[..16], "pack", relative, file.Sha1, file.Sha512, file.Size)); progress?.Report(new("安装整合包文件", ++count, descriptor.OverrideEntries.Count, relative));
        }
        archive.Verify(); await File.WriteAllBytesAsync(ContentInstallFiles.Literal(directory, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(new(1, "unregistered", installed)), ct);
        return descriptor.Inspection;
    }
}
