using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Read-only declaration inventory. It does not extract files, execute JARs or choose loader candidates.</summary>
public static class ModRelations
{
    // Match the legacy reader's bounded analysis, without imposing an import/file restriction.
    private const int MaxDepth = 5, MaxNodes = 512;
    private const long MaxEntry = 256L * 1024 * 1024, MaxHost = 512L * 1024 * 1024, MaxScan = 2L * 1024 * 1024 * 1024;
    private sealed class Budget { public long ScanBytes = MaxScan, HostBytes; public int Nodes; }
    private sealed record Provider(string Path, string Host, bool Enabled, bool Embedded);
    public static InstanceModRelations Read(GameInstance instance, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = InstanceWorkspace.Folder(instance.GameDirectory, "mods");
        var hosts = new List<ModRelationHost>(); var budget = new Budget();
        if (Directory.Exists(folder)) foreach (var file in new DirectoryInfo(folder).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!InstanceWorkspace.Accepts("mods", file.Name) || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var path = "mods/" + file.Name; var enabled = !file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
            budget.HostBytes = MaxHost; budget.Nodes = MaxNodes;
            ModDeclaration declaration;
            try
            {
                var size = file.Length; var modified = file.LastWriteTimeUtc;
                using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                declaration = ReadArchive(zip, path, instance.Loader, 0, budget, ct);
                file.Refresh();
                if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != modified)
                    declaration = declaration with { Ids = [], Dependencies = [], Children = [], Notices = ["文件在读取期间发生变化，请重新读取。"] };
            }
            catch (Exception ex) when (IsReadError(ex)) { declaration = Failed(path, "无法读取归档或模组声明。"); }
            hosts.Add(new(path, enabled, declaration));
        }
        return Build(hosts, ct);
    }
    public static InstanceModRelations Build(IReadOnlyList<ModRelationHost> hosts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var providers = new Dictionary<string, List<Provider>>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<(ModRelationHost Host, ModDeclaration Node)>(); var notices = new List<string>();
        foreach (var host in hosts) foreach (var node in Flatten(host.Declaration))
        {
            ct.ThrowIfCancellationRequested(); declarations.Add((host, node));
            foreach (var id in node.Ids)
            {
                if (!providers.TryGetValue(id, out var values)) providers[id] = values = [];
                values.Add(new(node.Path, host.RelativePath, host.Enabled, node.Path != host.RelativePath));
            }
            notices.AddRange(node.Notices.Select(n => node.Path + "：" + n));
        }
        var incomplete = declarations.Any(d => d.Host.Enabled && d.Node.Notices.Count > 0);
        var relations = new List<ModDependencyRelation>();
        foreach (var (host, node) in declarations) foreach (var dep in node.Dependencies.Where(d => !Platform(d.Id)))
        {
            ct.ThrowIfCancellationRequested(); var found = providers.GetValueOrDefault(dep.Id) ?? [];
            var status = found.Any(p => p.Enabled && !p.Embedded) ? ModDeclarationPresence.Independent
                : found.Any(p => p.Enabled) ? ModDeclarationPresence.Embedded
                : found.Count > 0 ? ModDeclarationPresence.Disabled
                : incomplete ? ModDeclarationPresence.Unverified : ModDeclarationPresence.NotFound;
            relations.Add(new(host.RelativePath, node.Path, node.Name, host.Enabled, dep, status, found.Select(p => p.Path).Distinct(StringComparer.Ordinal).ToArray()));
        }
        return new(hosts, relations, notices);
    }
    public static IEnumerable<ModDeclaration> Flatten(ModDeclaration root)
    { yield return root; foreach (var child in root.Children) foreach (var node in Flatten(child)) yield return node; }
    private static bool Platform(string id) => new[] { "minecraft", "java", "forge", "neoforge", "fabricloader", "quilt", "quilt_loader", "liteloader", "mcp" }.Contains(id, StringComparer.OrdinalIgnoreCase);
    private static ModDeclaration Failed(string path, string message) => new(path, Path.GetFileName(path), null, null, [], [], [], [message]);
    private static bool IsReadError(Exception ex) => ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException;
    private static ModDeclaration ReadArchive(ZipArchive zip, string path, string preferred, int depth, Budget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (--budget.Nodes < 0 || depth > MaxDepth) return Failed(path, "内嵌解析达到深度或节点预算，结果不完整。");
        if (zip.Entries.Count > 40000) return Failed(path, "归档条目过多，声明读取不完整。");
        var name = Path.GetFileName(path); string? version = null, loader = null;
        var ids = new List<string>(); var deps = new List<ModDeclaredDependency>(); var nested = new List<string>(); var notices = new List<string>();
        string Text(ZipArchiveEntry entry) { using var content = ReadEntry(entry, 128 * 1024, budget, ct); return Encoding.UTF8.GetString(content.ToArray()); }
        JsonNode? Json(ZipArchiveEntry entry) => JsonNode.Parse(Text(entry));
        try
        {
            var fabric = zip.GetEntry("fabric.mod.json"); var quilt = zip.GetEntry("quilt.mod.json");
            var forge = zip.GetEntry(preferred == "neoforge" ? "META-INF/neoforge.mods.toml" : "META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            var useForge = preferred is "forge" or "neoforge" or "cleanroom" && forge is not null;
            if (!useForge && quilt is not null && (preferred == "quilt" || fabric is null))
            {
                loader = "quilt"; var root = Json(quilt); var q = root?["quilt_loader"];
                name = Label(q?["metadata"]?["name"]) ?? Label(q?["id"]) ?? name; version = Label(q?["version"]); AddNodeId(q?["id"]);
                foreach (var alias in q?["provides"] as JsonArray ?? []) AddNodeId(alias is JsonValue ? alias : alias?["id"]);
                foreach (var dep in q?["depends"] as JsonArray ?? [])
                    if (dep is JsonValue) AddDep(Label(dep), "*", false);
                    else if (Label(dep?["id"]) is { } id) AddDep(id, Requirement(dep?["versions"]), (bool?)dep?["optional"] == true);
                    else notices.Add("存在未解析的 Quilt 组合依赖声明。");
                foreach (var jar in q?["jars"] as JsonArray ?? []) AddNested(jar is JsonValue ? Label(jar) : Label(jar?["file"]));
            }
            else if (!useForge && fabric is not null)
            {
                loader = "fabric"; var root = Json(fabric); name = Label(root?["name"]) ?? Label(root?["id"]) ?? name; version = Label(root?["version"]); AddNodeId(root?["id"]);
                foreach (var alias in root?["provides"] as JsonArray ?? []) AddNodeId(alias);
                foreach (var field in new[] { "depends", "recommends", "suggests" })
                    foreach (var dep in root?[field] as JsonObject ?? []) AddDep(dep.Key, Requirement(dep.Value), field != "depends");
                foreach (var jar in root?["jars"] as JsonArray ?? []) AddNested(Label(jar?["file"]));
            }
            else if (forge is not null)
            {
                loader = forge.FullName.Contains("neoforge", StringComparison.Ordinal) ? "neoforge" : "forge";
                var text = Text(forge); var blocks = Regex.Split(text, @"(?m)^\s*\[\[mods\]\]\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                foreach (var block in blocks.Skip(1)) { AddId(Field(block, "modId")); if (ids.Count == 1) { name = Field(block, "displayName") ?? name; version = Field(block, "version"); } }
                foreach (var block in Regex.Split(text, @"(?m)^\s*\[\[dependencies\.[^\]]+\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Skip(1))
                {
                    var type = Field(block, "type");
                    if (type is "incompatible" or "discouraged") continue;
                    AddDep(Field(block, "modId"), Field(block, "versionRange"), type != "required" && !Regex.IsMatch(block, @"(?m)^\s*mandatory\s*=\s*true", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
                }
            }
            else if (zip.GetEntry("mcmod.info") is { } legacy)
            {
                loader = "forge"; var root = Json(legacy); var mods = root as JsonArray ?? root?["modList"] as JsonArray ?? [];
                foreach (var mod in mods)
                {
                    AddNodeId(mod?["modid"]); if (ids.Count == 1) { name = Label(mod?["name"]) ?? name; version = Label(mod?["version"]); }
                    foreach (var dep in mod?["requiredMods"] as JsonArray ?? []) if (Label(dep)?.Split('@', 2) is { Length: > 0 } parts) AddDep(parts[0], parts.Length > 1 ? parts[1] : null, false);
                }
            }
            if (zip.GetEntry("META-INF/jarjar/metadata.json") is { } jarjar)
                foreach (var jar in Json(jarjar)?["jars"] as JsonArray ?? []) AddNested(Label(jar?["path"]));
        }
        catch (Exception ex) when (IsReadError(ex) || ex is RegexMatchTimeoutException) { notices.Add("部分模组声明无法解析，结果不完整。"); }
        var children = new List<ModDeclaration>();
        foreach (var nestedPath in nested.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); var childPath = path + "!/" + nestedPath;
            if (depth >= MaxDepth || budget.Nodes <= 0) { notices.Add("内嵌解析达到深度或节点预算，结果不完整。"); break; }
            try
            {
                if (nestedPath.StartsWith('/') || nestedPath.Contains('\\') || nestedPath.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException();
                using var input = ReadEntry(zip.GetEntry(nestedPath) ?? throw new InvalidDataException(), MaxEntry, budget, ct);
                using var child = new ZipArchive(input, ZipArchiveMode.Read);
                children.Add(ReadArchive(child, childPath, preferred, depth + 1, budget, ct));
            }
            catch (Exception ex) when (IsReadError(ex)) { children.Add(Failed(childPath, "内嵌归档缺失、损坏或超出读取预算，结果不完整。")); }
        }
        return new(path, name, version?.Contains("${", StringComparison.Ordinal) == true ? null : version, loader, ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), deps.Distinct().ToArray(), children, notices);
        void AddNodeId(JsonNode? node) { if (Label(node) is { } id) AddId(id); }
        void AddId(string? id) { if (!string.IsNullOrWhiteSpace(id)) ids.Add(id); }
        void AddDep(string? id, string? requirement, bool optional) { if (!string.IsNullOrWhiteSpace(id)) deps.Add(new(id, requirement, optional)); }
        void AddNested(string? file) { if (file is not null) nested.Add(file); else notices.Add("内嵌路径声明无效，结果不完整。"); }
    }
    private static string? Label(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length is > 0 and <= 1024 && !text.Any(char.IsControl) ? text : null;
    private static string? Requirement(JsonNode? value) => Label(value) ?? value?.ToJsonString();
    private static string? Field(string text, string key)
    { var value = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Groups[1].Value; return value.Length > 0 ? value : null; }
    private static MemoryStream ReadEntry(ZipArchiveEntry entry, long limit, Budget budget, CancellationToken ct)
    {
        if (entry.Length < 0 || entry.Length > limit || entry.Length > budget.HostBytes || entry.Length > budget.ScanBytes) throw new InvalidDataException();
        var output = new MemoryStream();
        try
        {
            using var input = entry.Open(); var bytes = new byte[65536];
            while (true) { ct.ThrowIfCancellationRequested(); var n = input.Read(bytes); if (n == 0) break; budget.HostBytes -= n; budget.ScanBytes -= n; if (output.Length + n > limit || budget.HostBytes < 0 || budget.ScanBytes < 0) throw new InvalidDataException(); output.Write(bytes, 0, n); }
            if (output.Length != entry.Length) throw new InvalidDataException(); output.Position = 0; return output;
        }
        catch { output.Dispose(); throw; }
    }
}
