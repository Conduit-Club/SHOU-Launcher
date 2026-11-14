using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Catalog;

/// <summary>Uses a caller-owned no-redirect transport. API credentials never enter CDN requests.</summary>
public sealed class CurseForgeCatalog(HttpClient http, string? apiKey)
{
    private static readonly IReadOnlyDictionary<string, int> Classes = new Dictionary<string, int>
    { ["mod"] = 6, ["modpack"] = 4471, ["resourcepack"] = 12, ["shader"] = 6552, ["datapack"] = 6945, ["world"] = 17 };
    public static bool IsProject(string id) => id.StartsWith("cf-", StringComparison.Ordinal);
    private static string Id(string id)
    {
        var number = id.StartsWith("cf-", StringComparison.Ordinal) ? id[3..] : "";
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0) throw new InvalidDataException("CurseForge 项目身份无效。");
        return value.ToString(CultureInfo.InvariantCulture);
    }
    private async Task<JsonNode> ReadAsync(string path, CancellationToken ct, object? body = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("尚未配置 CurseForge API；可选择 Modrinth，或在项目构建配置中提供 API key。");
        if (apiKey.Length > 8192 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new InvalidOperationException("CurseForge API 配置格式无效。");
        if (http.DefaultRequestHeaders.Contains("x-api-key") || http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie")) throw new InvalidOperationException("目录客户端不能携带全局凭据。");
        var uri = new Uri("https://api.curseforge.com/v1/" + path);
        if (uri.Host != "api.curseforge.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new InvalidDataException("CurseForge API 地址无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (body is not null) request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", apiKey); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.6.1 (+https://github.com/Conduit-Club/SHOU-Launcher)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != uri || (int)response.StatusCode is >= 300 and < 400) throw new IOException("CurseForge API 重定向已拒绝。");
        if (!response.IsSuccessStatusCode) throw new IOException($"CurseForge 返回 HTTP {(int)response.StatusCode}，请稍后重试。");
        if (response.Content.Headers.ContentLength is > 6 * 1024 * 1024) throw new InvalidDataException("CurseForge 响应超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (count == 0) break; if (bytes.Length + count > 6 * 1024 * 1024) throw new InvalidDataException("CurseForge 响应超过大小限制。"); bytes.Write(buffer, 0, count); }
        return JsonNode.Parse(bytes.ToArray())?["data"] ?? throw new InvalidDataException("CurseForge 目录结构无效。");
    }
    public async Task<IReadOnlyList<ContentProject>> SearchAsync(ContentQuery query, CancellationToken ct)
    {
        if (!Classes.TryGetValue(query.Kind, out var classId) || query.Search.Length > 256 || query.Offset < 0 || query.Limit is < 1 or > 50 || query.Offset + query.Limit > 10000) throw new ArgumentException("搜索条件超出范围。");
        var sort = query.Sort switch { "relevance" => query.Search.Length == 0 ? 6 : 2, "downloads" => 6, "updated" => 3, "newest" => 11, "follows" => 2, _ => throw new ArgumentException("未知排序。") };
        var path = $"mods/search?gameId=432&classId={classId}&pageSize={query.Limit}&index={query.Offset}&sortField={sort}&sortOrder=desc&searchFilter=" + Uri.EscapeDataString(query.Search);
        if (!string.IsNullOrWhiteSpace(query.GameVersion)) path += "&gameVersion=" + Uri.EscapeDataString(query.GameVersion);
        // The API supports a loader without a game version through the plural parameter.
        if (!string.IsNullOrWhiteSpace(query.Loader)) path += "&modLoaderTypes=" + Uri.EscapeDataString("[" + LoaderId(query.Loader) + "]");
        if (!string.IsNullOrEmpty(query.Category)) path += "&categoryId=" + Id(query.Category);
        var data = await ReadAsync(path, ct).ConfigureAwait(false) as JsonArray ?? throw new InvalidDataException("CurseForge 搜索响应无效。");
        if (data.Count > query.Limit) throw new InvalidDataException("CurseForge 搜索数量超限。");
        return data.Select(n => ParseProject(n, query.Kind)).ToArray();
    }
    private static int LoaderId(string loader) => loader.ToLowerInvariant() switch { "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => throw new ArgumentException("未知加载器。") };
    private static string Text(JsonNode? node, string key) => (string?)node?[key] ?? "";
    private static ContentProject ParseProject(JsonNode? n, string? expectedKind = null)
    {
        var number = (int?)n?["id"] ?? 0; if (number <= 0 || (int?)n?["gameId"] != 432) throw new InvalidDataException("CurseForge 项目身份无效。");
        var kind = Classes.FirstOrDefault(p => p.Value == (int?)n?["classId"]).Key ?? throw new InvalidDataException("不支持的 CurseForge 资源类型。");
        if (expectedKind is not null && expectedKind != kind) throw new InvalidDataException("CurseForge 搜索返回了其它类型资源。");
        var slug = SafePath.Identifier(Text(n, "slug"));
        var page = new Uri(Text(n?["links"], "websiteUrl"));
        if (page.Scheme != "https" || !page.IsDefaultPort || page.Host is not ("www.curseforge.com" or "curseforge.com") || page.UserInfo.Length != 0 || page.Fragment.Length != 0) throw new InvalidDataException("CurseForge 项目链接无效。");
        var versions = (n?["latestFilesIndexes"] as JsonArray ?? []).Select(x => Text(x, "gameVersion")).Where(v => v.Length is > 0 and < 160).Distinct().ToArray();
        return new("cf-" + number, slug, Text(n, "name"), Text(n, "summary"), string.Join(", ", (n?["authors"] as JsonArray ?? []).Take(20).Select(a => Text(a, "name"))),
            (long?)n?["downloadCount"] ?? 0, kind, (string?)n?["logo"]?["thumbnailUrl"], page,
            Categories: (n?["categories"] as JsonArray ?? []).Take(30).Select(a => Text(a, "name")).ToArray(), GameVersions: versions,
            Updated: DateTimeOffset.TryParse(Text(n, "dateModified"), out var date) ? date : null, Source: "curseforge");
    }
    public async Task<ContentProject> ProjectAsync(string id, CancellationToken ct) => ParseProject(await ReadAsync("mods/" + Id(id), ct).ConfigureAwait(false));
    public async Task<IReadOnlyList<ContentCategory>> CategoriesAsync(CancellationToken ct)
    {
        var data = await ReadAsync("categories?gameId=432", ct).ConfigureAwait(false) as JsonArray ?? throw new InvalidDataException("CurseForge 分类无效。");
        return data.Where(n => (bool?)n?["isClass"] != true && (int?)n?["id"] > 0).Select(n => new ContentCategory("cf-" + (int)n!["id"]!, Classes.FirstOrDefault(p => p.Value == (int?)n?["classId"]).Key ?? "", Text(n, "name"), "curseforge"))
            .Where(c => c.Kind.Length > 0).ToArray();
    }
    public async Task<IReadOnlyList<ContentRelease>> ReleasesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var id = Id(projectId); var path = "mods/" + id + "/files?pageSize=50";
        if (!string.IsNullOrWhiteSpace(gameVersion)) path += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
        if (!string.IsNullOrWhiteSpace(loader)) path += "&modLoaderType=" + LoaderId(loader);
        var data = await ReadAsync(path, ct).ConfigureAwait(false) as JsonArray ?? throw new InvalidDataException("CurseForge 文件列表无效。");
        return data.Take(50).Select(n => ParseRelease(n, int.Parse(id, CultureInfo.InvariantCulture))).ToArray();
    }
    public async Task<ContentRelease> ReleaseAsync(string releaseId, CancellationToken ct)
    {
        var parts = releaseId.Split('-'); if (parts.Length != 3 || parts[0] != "cf") throw new InvalidDataException("CurseForge 文件身份无效。");
        var project = Id("cf-" + parts[1]); var file = Id("cf-" + parts[2]);
        return ParseRelease(await ReadAsync($"mods/{project}/files/{file}", ct).ConfigureAwait(false), int.Parse(project, CultureInfo.InvariantCulture));
    }
    public async Task<IReadOnlyDictionary<uint, ContentRelease>> MatchFingerprintsAsync(IReadOnlyList<uint> fingerprints, CancellationToken ct)
    {
        var result = new Dictionary<uint, ContentRelease>();
        foreach (var group in fingerprints.Distinct().Chunk(100))
        {
            var data = await ReadAsync("fingerprints/432", ct, new { fingerprints = group });
            foreach (var match in data["exactMatches"] as JsonArray ?? [])
            {
                var file = match?["file"]; var project = (int?)file?["modId"] ?? 0; var fingerprint = (uint?)file?["fileFingerprint"] ?? 0;
                if (project <= 0 || (int?)match?["id"] != project || !group.Contains(fingerprint)) throw new InvalidDataException("CurseForge 指纹身份无效。");
                result[fingerprint] = ParseRelease(file, project);
            }
        }
        return result;
    }
    private static ContentRelease ParseRelease(JsonNode? n, int project)
    {
        var fileId = (int?)n?["id"] ?? 0; if (fileId <= 0 || (int?)n?["modId"] != project || (int?)n?["gameId"] != 432) throw new InvalidDataException("CurseForge 文件身份不匹配。");
        var name = Text(n, "fileName"); var sha = (n?["hashes"] as JsonArray ?? []).FirstOrDefault(h => (int?)h?["algo"] == 1)?["value"]?.GetValue<string>() ?? "";
        var size = (long?)n?["fileLength"] ?? -1; var address = Text(n, "downloadUrl");
        var files = new List<ContentFile>(); string? notice = null; CurseForgeManualFile? manual = null;
        if ((int?)n?["fileStatus"] is 5 or 6 or 7) notice = "此文件已被官方拒绝、标记为恶意或删除，不能下载。";
        else if ((bool?)n?["isServerPack"] == true) notice = "此文件是服务器包，请选择客户端版本。";
        else if ((bool?)n?["isAvailable"] == false) notice = "此版本当前不可下载，请查看项目页。";
        else if (address.Length == 0)
        {
            notice = "作者未提供第三方下载地址，请在官网下载安装文件后继续。";
            if (ContentInstallFiles.Hash(sha, 40) && size > 0 && name.Length > 0 && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
                manual = new(project, fileId, name, sha.ToLowerInvariant(), size, new Uri($"https://www.curseforge.com/projects/{project}/files/{fileId}"));
        }
        else
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !CurseForgePackResolver.AllowedCurseForgeUri(uri)
                || !ContentInstallFiles.Hash(sha, 40) || size is <= 0 or > ContentInstallPlanner.MaximumTotalBytes
                || name.Length is 0 or > 200 || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':'))
                notice = "此版本缺少有效的官方文件来源、SHA-1 或大小，暂不可自动下载；其它版本仍可选择。";
            else files.Add(new(name, uri, sha.ToLowerInvariant(), size, true));
        }
        var tags = (n?["gameVersions"] as JsonArray ?? []).Select(x => (string?)x ?? "").Take(500).ToArray();
        var loaders = tags.Where(v => v.ToLowerInvariant() is "forge" or "fabric" or "neoforge" or "quilt").Select(v => v.ToLowerInvariant()).ToArray();
        var dependencies = (n?["dependencies"] as JsonArray ?? []).Select(d => new ContentDependency((int?)d?["relationType"] switch { 3 => "required", 2 => "optional", 5 => "incompatible", 1 or 4 => "embedded", _ => "unknown" }, "cf-" + (int?)d?["modId"])).ToArray();
        return new($"cf-{project}-{fileId}", Text(n, "displayName"), name, DateTimeOffset.TryParse(Text(n, "fileDate"), out var date) ? date : DateTimeOffset.MinValue,
            tags.Where(v => v.Length > 0 && char.IsAsciiDigit(v[0])).ToArray(), loaders, files, dependencies, "cf-" + project, Notice: notice,
            ReleaseChannel: (int?)n?["releaseType"] switch { 2 => "beta", 3 => "alpha", _ => "release" }, ManualFile: manual);
    }
}
