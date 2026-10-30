using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.Conduit;

public enum SourceCategory { Minecraft, Loader, Mod }
public enum SourceLayout { Official, Bmclapi, Shou, Prefix }

/// <summary>Priority 越小越优先；Prefix 源按 URL 路径边界替换，保留路径和查询串。</summary>
public sealed class DownloadSource
{
    public string Name { get; set; } = "自定义源";
    public SourceCategory Category { get; set; }
    public SourceLayout Layout { get; set; } = SourceLayout.Prefix;
    public string BaseUrl { get; set; } = "";
    public string OriginPrefix { get; set; } = "";
    public int Priority { get; set; } = 300;
    public bool Enabled { get; set; } = true;
}

public sealed record SourceAddress(string Url, string Name, int Priority, bool IsPartialCatalog = false);

public static class DownloadSources
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string ShouRoot = "https://minecraft.aaron212.com";
    public const string BmclRoot = "https://bmclapi2.bangbang93.com";

    public static List<DownloadSource> Defaults() =>
    [
        new() { Name = "BMCLAPI · Minecraft", Category = SourceCategory.Minecraft, Layout = SourceLayout.Bmclapi, BaseUrl = BmclRoot, Priority = 0 },
        new() { Name = "社团镜像 · SHOU-MCMirror", Category = SourceCategory.Minecraft, Layout = SourceLayout.Shou, BaseUrl = ShouRoot, Priority = 100 },
        new() { Name = "Mojang 官方", Category = SourceCategory.Minecraft, Layout = SourceLayout.Official, Priority = 200 },
        new() { Name = "BMCLAPI · 加载器", Category = SourceCategory.Loader, Layout = SourceLayout.Bmclapi, BaseUrl = BmclRoot, Priority = 0 },
        new() { Name = "加载器官方", Category = SourceCategory.Loader, Layout = SourceLayout.Official, Priority = 200 },
        new() { Name = "Mod 官方 · CurseForge / Modrinth", Category = SourceCategory.Mod, Layout = SourceLayout.Official, Priority = 0 },
        new() { Name = "MCIM · Modrinth API", Category = SourceCategory.Mod, OriginPrefix = "https://api.modrinth.com", BaseUrl = "https://mod.mcimirror.top/modrinth", Priority = 300 },
        new() { Name = "MCIM · CurseForge API", Category = SourceCategory.Mod, OriginPrefix = "https://api.curseforge.com", BaseUrl = "https://mod.mcimirror.top/curseforge", Priority = 300 },
        new() { Name = "MCIM · Modrinth 文件", Category = SourceCategory.Mod, OriginPrefix = "https://cdn.modrinth.com", BaseUrl = "https://mod.mcimirror.top", Priority = 300 },
        new() { Name = "MCIM · CurseForge 文件", Category = SourceCategory.Mod, OriginPrefix = "https://edge.forgecdn.net", BaseUrl = "https://mod.mcimirror.top", Priority = 300 }
    ];

    public static List<DownloadSource> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Defaults();
        var sources = JsonSerializer.Deserialize<List<DownloadSource>>(json) ?? throw new ArgumentException("源配置为空。");
        foreach (var source in sources) Validate(source);
        return sources;
    }

    public static void Validate(DownloadSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Name)) throw new ArgumentException("请填写源名称。");
        if (!Enum.IsDefined(source.Category) || !Enum.IsDefined(source.Layout)) throw new ArgumentException("源类型无效。");
        if (source.Layout != SourceLayout.Official && !IsHttp(source.BaseUrl)) throw new ArgumentException("镜像地址必须是 HTTP(S) URL，且不含用户名、密码、查询串或片段。");
        if (source.Layout == SourceLayout.Prefix && !IsHttp(source.OriginPrefix)) throw new ArgumentException("请填写需要替换的官方 URL 前缀。");
        if (source.Layout == SourceLayout.Shou && source.Category != SourceCategory.Minecraft) throw new ArgumentException("SHOU-MCMirror 仅支持 Minecraft。");
        if (source.Layout == SourceLayout.Bmclapi && source.Category == SourceCategory.Mod) throw new ArgumentException("BMCLAPI 不提供 Mod 镜像。");
    }

    private static bool IsHttp(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http") && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static bool Matches(string url, string prefix) => url.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        || url.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith(prefix.TrimEnd('/') + "?", StringComparison.OrdinalIgnoreCase);

    private static string ReplacePrefix(string url, string prefix, string replacement)
        => replacement.TrimEnd('/') + url[prefix.TrimEnd('/').Length..];

    private static SourceCategory? Category(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (new[] { "api.modrinth.com", "cdn.modrinth.com", "api.curseforge.com", "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }.Contains(host)) return SourceCategory.Mod;
        if (new[] { "meta.fabricmc.net", "maven.fabricmc.net", "meta.quiltmc.org", "maven.quiltmc.org", "meta.legacyfabric.net", "maven.minecraftforge.net", "files.minecraftforge.net", "maven.neoforged.net", "dl.liteloader.com", "optifine.net" }.Contains(host)) return SourceCategory.Loader;
        if (new[] { "piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com", "resources.download.minecraft.net", "libraries.minecraft.net" }.Contains(host)) return SourceCategory.Minecraft;
        return null;
    }

    private static string Canonical(string url, IReadOnlyList<DownloadSource> sources)
    {
        if (Matches(url, "https://mediafilez.forgecdn.net/files")) return ReplacePrefix(url, "https://mediafilez.forgecdn.net", "https://edge.forgecdn.net");
        if (Matches(url, "https://mod.mcimirror.top/files")) return ReplacePrefix(url, "https://mod.mcimirror.top", "https://edge.forgecdn.net");
        if (Matches(url, "https://mod.mcimirror.top/data")) return ReplacePrefix(url, "https://mod.mcimirror.top", "https://cdn.modrinth.com");
        foreach (var source in sources.Where(s => s.Layout == SourceLayout.Shou).Append(new DownloadSource { Layout = SourceLayout.Shou, BaseUrl = ShouRoot }))
        {
            if (Matches(url, source.BaseUrl + "/metadata/version_manifest_v2.json")) return ManifestUrl;
            var prefix = source.BaseUrl.TrimEnd('/') + "/upstream/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "https://" + url[prefix.Length..];
        }
        // Reverse longest prefix first, including disabled rules: old metadata remains portable after changing priority.
        foreach (var source in sources.Where(s => s.Layout == SourceLayout.Prefix && !string.IsNullOrWhiteSpace(s.BaseUrl)).OrderByDescending(s => s.BaseUrl.Length))
            if (Matches(url, source.BaseUrl) && !string.IsNullOrWhiteSpace(source.OriginPrefix))
                return ReplacePrefix(url, source.BaseUrl, source.OriginPrefix);
        foreach (var root in sources.Where(s => s.Layout == SourceLayout.Bmclapi).Select(s => s.BaseUrl).Append(BmclRoot).Distinct())
        {
            if (!Matches(url, root)) continue;
            var path = url[root.TrimEnd('/').Length..];
            if (path.StartsWith("/mc/game/version_manifest")) return ManifestUrl;
            if (path.StartsWith("/fabric-meta/")) return "https://meta.fabricmc.net" + path[12..];
            if (path.StartsWith("/maven/net/minecraftforge/")) return "https://maven.minecraftforge.net" + path[6..];
            if (path.StartsWith("/maven/net/neoforged/")) return "https://maven.neoforged.net/releases" + path[6..];
            if (path.StartsWith("/maven/net/fabricmc/")) return "https://maven.fabricmc.net" + path[6..];
            if (path.StartsWith("/maven/")) return "https://libraries.minecraft.net" + path[6..];
            if (path.StartsWith("/libraries/")) return "https://libraries.minecraft.net" + path[10..];
            if (path.StartsWith("/assets/") && !path.StartsWith("/assets/v1/")) return "https://resources.download.minecraft.net" + path[7..];
            if (path.StartsWith("/assets/v1/")) return "https://piston-meta.mojang.com" + path[7..];
            if (path.StartsWith("/v1/packages/")) return "https://piston-meta.mojang.com" + path;
            if (path.StartsWith("/v1/objects/") || path.StartsWith("/objects/")) return "https://piston-data.mojang.com" + path;
            return url; // BMCL-specific API; its official alternative uses a different response format.
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && Category(url) == SourceCategory.Minecraft && uri.AbsolutePath.Contains("/mc/game/version_manifest")) return ManifestUrl;
        return url;
    }

    public static IReadOnlyList<SourceAddress> Resolve(IEnumerable<string> urls, IReadOnlyList<DownloadSource> sources)
    {
        var result = new List<SourceAddress>();
        var originals = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        var hasShouMetadata = originals.Any(url => sources.Any(s => s.Layout == SourceLayout.Shou && Matches(url, s.BaseUrl.TrimEnd('/') + "/metadata/versions")));
        foreach (var original in originals)
        {
            var metadataSource = sources.FirstOrDefault(s => s.Layout == SourceLayout.Shou && Matches(original, s.BaseUrl.TrimEnd('/') + "/metadata/versions"));
            if (metadataSource != null)
            {
                if (metadataSource.Enabled) result.Add(new(original, metadataSource.Name, metadataSource.Priority));
                continue; // SHOU hashes the rewritten JSON; it cannot be inferred from Mojang's package hash.
            }
            var url = Canonical(original, sources);
            var category = Category(url);
            var bmclRoot = sources.Where(s => s.Layout == SourceLayout.Bmclapi).Select(s => s.BaseUrl).Append(BmclRoot).FirstOrDefault(root => Matches(url, root));
            if (bmclRoot != null) category = SourceCategory.Loader;
            foreach (var source in sources.Where(s => s.Enabled).OrderBy(s => s.Priority))
            {
                if (source.Layout == SourceLayout.Prefix)
                {
                    if (Matches(url, source.OriginPrefix)) result.Add(new(ReplacePrefix(url, source.OriginPrefix, source.BaseUrl), source.Name, source.Priority));
                    continue;
                }
                if (source.Category != category) continue;
                if (source.Layout == SourceLayout.Official && bmclRoot == null) result.Add(new(url, source.Name, source.Priority));
                else if (source.Layout == SourceLayout.Bmclapi)
                {
                    var mapped = BmclUrl(url, source.BaseUrl, bmclRoot);
                    if (mapped != null) result.Add(new(mapped, source.Name, source.Priority));
                }
                else if (source.Layout == SourceLayout.Shou && category == SourceCategory.Minecraft)
                {
                    if (hasShouMetadata && url != ManifestUrl) continue;
                    var mapped = url == ManifestUrl ? source.BaseUrl.TrimEnd('/') + "/metadata/version_manifest_v2.json"
                        : source.BaseUrl.TrimEnd('/') + "/upstream/" + new Uri(url).Host + new Uri(url).PathAndQuery;
                    result.Add(new(mapped, source.Name, source.Priority, url == ManifestUrl));
                }
            }
            // Unknown URLs (including rewritten manifest URLs) are never lost. Explicitly disabled built-ins stay disabled.
            if (category == null) result.Add(new(url, "原始地址", 200));
        }
        return result.OrderBy(s => s.Priority).DistinctBy(s => s.Url).ToArray();
    }

    private static string? BmclUrl(string url, string root, string? existingRoot)
    {
        root = root.TrimEnd('/');
        if (existingRoot != null) return ReplacePrefix(url, existingRoot, root);
        if (url == ManifestUrl) return root + "/mc/game/version_manifest.json";
        var uri = new Uri(url);
        return uri.Host switch
        {
            "resources.download.minecraft.net" => root + "/assets" + uri.PathAndQuery,
            "libraries.minecraft.net" or "maven.minecraftforge.net" or "maven.fabricmc.net" => root + "/maven" + uri.PathAndQuery,
            "maven.neoforged.net" when uri.AbsolutePath.StartsWith("/releases/") => root + "/maven" + uri.PathAndQuery[9..],
            "meta.fabricmc.net" => root + "/fabric-meta" + uri.PathAndQuery,
            "piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com" or "launchermeta.mojang.com" => root + uri.PathAndQuery,
            _ => null
        };
    }

    /// <summary>允许小型清单；仅要求版本项完整，并检查请求的目标版本。</summary>
    public static JsonObject ValidateManifest(string json, string? requiredVersion = null)
    {
        var manifest = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("版本清单不是 JSON 对象。");
        if (manifest["versions"] is not JsonArray { Count: > 0 } versions) throw new FormatException("版本清单为空。");
        foreach (var entry in versions)
            if (string.IsNullOrEmpty((string?)entry?["id"]) || !Uri.TryCreate((string?)entry?["url"], UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new FormatException("版本清单含有无效条目。");
        if (!string.IsNullOrEmpty(requiredVersion) && !versions.Any(v => (string?)v?["id"] == requiredVersion)) throw new KeyNotFoundException($"此源未收录 Minecraft {requiredVersion}。");
        return manifest;
    }

    public static (SourceAddress Source, JsonObject Manifest) FindManifest(IReadOnlyList<SourceAddress> sources,
        Func<string, string> fetch, string? requiredVersion = null,
        Action<SourceAddress, Exception>? onFailure = null)
    {
        var failures = new List<Exception>();
        // A curated mirror is useful for files and per-version fallback, but must not
        // hide the full catalog after a transient failure of the preferred mirror.
        var ordered = string.IsNullOrEmpty(requiredVersion)
            ? sources.OrderBy(s => s.IsPartialCatalog).ThenBy(s => s.Priority)
            : sources.OrderBy(s => s.Priority);
        foreach (var source in ordered)
            try { return (source, ValidateManifest(fetch(source.Url), requiredVersion)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex); onFailure?.Invoke(source, ex); }
        throw new AggregateException("所有 Minecraft 清单源均不可用或缺少目标版本。", failures);
    }
}
