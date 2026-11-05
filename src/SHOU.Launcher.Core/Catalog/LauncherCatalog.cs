using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Catalog;

/// <summary>Data-only catalogs share the production proxy/source policy. They never execute remote content.</summary>
public sealed class LauncherCatalog(HttpClient http, Func<IReadOnlyList<DownloadSource>> sources)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private async Task<(JsonNode Data, string Source, bool Partial)> ReadAsync(string url, bool modSource, CancellationToken ct, Func<JsonNode, bool> validate)
    {
        var candidates = modSource ? ModDownloadSources.Resolve(url, sources()).Select(u => new SourceAddress(u, "Modrinth", 0)).ToArray()
            : DownloadSources.Resolve([url], sources());
        var failures = new List<Exception>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.5.0 (+https://github.com/Conduit-Club/SHOU-Launcher)");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumBytes) throw new InvalidDataException("目录响应超过大小限制。");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[16384];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("目录响应超过大小限制。");
                    bytes.Write(buffer, 0, count);
                }
                var node = JsonNode.Parse(bytes.ToArray()) ?? throw new InvalidDataException("目录内容为空。");
                if (!validate(node)) throw new InvalidDataException("目录结构无效。");
                return (node, new Uri(candidate.Url).Host, candidate.IsPartialCatalog);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            { failures.Add(new IOException("目录来源失败：" + ex.GetType().Name)); }
        }
        throw new AggregateException("所有目录来源均不可用，请检查网络后重试。", failures);
    }
    public async Task<GameCatalog> GamesAsync(CancellationToken ct = default)
    {
        var document = await ReadAsync(DownloadSources.ManifestUrl, false, ct, n => n["versions"] is JsonArray).ConfigureAwait(false);
        var data = document.Data;
        var entries = data["versions"] as JsonArray ?? throw new InvalidDataException("版本列表缺失。");
        if (entries.Count is 0 or > 20000) throw new InvalidDataException("版本列表数量无效。");
        var latest = new[] { (string?)data["latest"]?["release"], (string?)data["latest"]?["snapshot"] };
        var result = entries.Select(n =>
        {
            var id = (string?)n?["id"] ?? "";
            if (id.Length is 0 or > 160 || id.Any(char.IsControl)) throw new InvalidDataException("版本名称无效。");
            var installable = true;
            try { _ = SafePath.Identifier(id); } catch (ArgumentException) { installable = false; }
            return new GameCatalogVersion(id, (string?)n?["type"] ?? "unknown",
                DateTimeOffset.TryParse((string?)n?["releaseTime"], out var date) ? date : DateTimeOffset.MinValue,
                latest.Contains(id), installable);
        }).ToArray();
        return new(result, document.Source, document.Partial);
    }
    public async Task<IReadOnlyList<LoaderCatalogVersion>> FabricAsync(string gameVersion, CancellationToken ct = default)
    {
        SafePath.Identifier(gameVersion);
        var document = await ReadAsync("https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(gameVersion), false, ct, n => n is JsonArray).ConfigureAwait(false);
        var data = document.Data;
        if (data is not JsonArray entries || entries.Count > 5000) throw new InvalidDataException("Fabric目录无效。");
        return entries.Select(n => new LoaderCatalogVersion(SafePath.Identifier((string?)n?["loader"]?["version"] ?? ""),
            (bool?)n?["loader"]?["stable"] ?? false)).Distinct().ToArray();
    }
    public async Task<IReadOnlyList<ContentProject>> SearchAsync(ContentQuery query, CancellationToken ct = default)
    {
        var type = query.Kind switch { "mod" => "mod", "modpack" => "modpack", "resourcepack" => "resourcepack",
            "shader" => "shader", "datapack" => "mod", _ => throw new NotSupportedException("此来源尚不支持这类内容。") };
        if (query.Search.Length > 256 || query.Offset is < 0 or > 10000) throw new ArgumentException("搜索条件超出范围。");
        if (query.Sort is not ("relevance" or "downloads" or "follows" or "newest" or "updated")) throw new ArgumentException("未知排序。");
        var facets = new List<string[]> { new[] { "project_type:" + type } };
        if (query.Kind == "datapack") facets.Add(["categories:datapack"]);
        if (!string.IsNullOrWhiteSpace(query.GameVersion)) facets.Add(["versions:" + query.GameVersion]);
        if (!string.IsNullOrWhiteSpace(query.Loader)) facets.Add(["categories:" + query.Loader]);
        var url = "https://api.modrinth.com/v2/search?limit=30&offset=" + query.Offset + "&query=" + Uri.EscapeDataString(query.Search)
            + "&index=" + query.Sort + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets));
        var document = await ReadAsync(url, true, ct, n => n["hits"] is JsonArray).ConfigureAwait(false);
        var data = document.Data;
        if (data["hits"] is not JsonArray hits || hits.Count > 100) throw new InvalidDataException("资源搜索响应无效。");
        return hits.Select(n =>
        {
            var id = SafePath.Identifier((string?)n?["project_id"] ?? "");
            var slug = SafePath.Identifier((string?)n?["slug"] ?? id);
            return new ContentProject(id, slug, (string?)n?["title"] ?? id, (string?)n?["description"] ?? "",
                (string?)n?["author"] ?? "", (long?)n?["downloads"] ?? 0, query.Kind, (string?)n?["icon_url"],
                new Uri("https://modrinth.com/" + type + "/" + slug));
        }).ToArray();
    }
    public async Task<IReadOnlyList<ContentRelease>> ReleasesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        SafePath.Identifier(projectId);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion)) args.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (!string.IsNullOrWhiteSpace(loader)) args.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
        var document = await ReadAsync("https://api.modrinth.com/v2/project/" + projectId + "/version" + (args.Count == 0 ? "" : "?" + string.Join('&', args)), true, ct, n => n is JsonArray).ConfigureAwait(false);
        var data = document.Data;
        if (data is not JsonArray versions || versions.Count > 10000) throw new InvalidDataException("资源版本响应无效。");
        return versions.Take(250).Select(n => new ContentRelease(SafePath.Identifier((string?)n?["id"] ?? ""), (string?)n?["name"] ?? "",
            (string?)n?["version_number"] ?? "", DateTimeOffset.TryParse((string?)n?["date_published"], out var date) ? date : DateTimeOffset.MinValue,
            Strings(n?["game_versions"]), Strings(n?["loaders"]), (n?["files"] as JsonArray ?? []).Select(f =>
            {
                var name = (string?)f?["filename"] ?? "";
                if (name.Length is 0 or > 200 || name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new InvalidDataException("资源文件名无效。");
                var uri = new Uri((string?)f?["url"] ?? "");
                if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("资源文件来源无效。");
                var hash = (string?)f?["hashes"]?["sha1"] ?? "";
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源文件缺少SHA-1。");
                var size = (long?)f?["size"] ?? -1;
                if (size is < 0 or > 2147483648) throw new InvalidDataException("资源文件大小无效。");
                return new ContentFile(name, uri, hash.ToLowerInvariant(), size, (bool?)f?["primary"] ?? false);
            }).ToArray())).ToArray();
    }
    private static string[] Strings(JsonNode? node) => node is JsonArray values ? values.Select(v => (string?)v ?? "").ToArray() : [];
    public async Task<byte[]> IconAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "cdn.modrinth.com" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("资源图标地址不受支持。");
        foreach (var candidate in ModDownloadSources.Resolve(url, sources()))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.5.0");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 524288) throw new InvalidDataException("资源图标过大。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var result = new MemoryStream(); var buffer = new byte[8192];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (count == 0) break;
                    if (result.Length + count > 524288) throw new InvalidDataException("资源图标过大。");
                    result.Write(buffer, 0, count);
                }
                return result.ToArray();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
        }
        throw new IOException("资源图标暂不可用。");
    }
}
