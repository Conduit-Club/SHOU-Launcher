using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IContentSourceCatalogService
{
    private HttpClient ContentTransport() => new(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
    public async Task<ContentSearchPage> SearchContentPageAsync(ContentQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (query.Source is not ("all" or "modrinth" or "curseforge")) throw new ArgumentException("未知资源来源。");
        var mr = query.Kind != "world" && query.Source != "curseforge" && query.Category?.StartsWith("cf-", StringComparison.Ordinal) != true;
        var cf = query.Source != "modrinth" && (string.IsNullOrEmpty(query.Category) || query.Category.StartsWith("cf-", StringComparison.Ordinal));
        if (!mr && !cf) throw new NotSupportedException("此来源没有对应的资源分类，请更换来源。");
        var parts = mr && cf ? query with { Limit = 15, Offset = query.Offset / 2 } : query;
        using var transport = ContentTransport();
        var sources = new List<(string Name, Task<IReadOnlyList<ContentProject>> Read)>();
        if (mr) sources.Add(("Modrinth", Catalog.SearchAsync(parts, cancellationToken)));
        if (cf) sources.Add(("CurseForge", new CurseForgeCatalog(transport, build.CurseForgeApiKey).SearchAsync(parts, cancellationToken)));
        var results = new List<ContentProject>(); var notices = new List<string>(); var failures = new List<Exception>();
        foreach (var source in sources)
        {
            try { results.AddRange(await source.Read.ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException)
            { failures.Add(ex); notices.Add(source.Name + " 暂时不可用，可刷新重试或切换来源。"); }
        }
        if (failures.Count == sources.Count) throw new IOException(string.Join(" ", notices), failures[0]);
        IEnumerable<ContentProject> ordered = query.Sort switch
        {
            "downloads" => results.OrderByDescending(p => p.Downloads),
            "updated" => results.OrderByDescending(p => p.Updated),
            "relevance" when query.Search.Length == 0 => results.OrderByDescending(p => p.Downloads),
            _ => Interleave(results.Where(p => p.Source == "modrinth").ToArray(), results.Where(p => p.Source == "curseforge").ToArray())
        };
        return new(ordered.ToArray(), notices.Count > 0 ? string.Join(" ", notices) : null);
    }
    private static IEnumerable<ContentProject> Interleave(ContentProject[] first, ContentProject[] second)
    { for (var i = 0; i < Math.Max(first.Length, second.Length); i++) { if (i < first.Length) yield return first[i]; if (i < second.Length) yield return second[i]; } }
    private async Task<IReadOnlyList<ContentCategory>> AllContentCategoriesAsync(CancellationToken ct)
    {
        using var transport = ContentTransport(); var results = new List<ContentCategory>();
        foreach (var read in new[] { Catalog.CategoriesAsync(ct), new CurseForgeCatalog(transport, build.CurseForgeApiKey).CategoriesAsync(ct) })
        {
            try { results.AddRange(await read.ConfigureAwait(false)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException) { }
        }
        return results;
    }
    private async Task<byte[]> CurseForgeIconAsync(Uri uri, CancellationToken ct)
    {
        using var transport = ContentTransport();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 524288 || response.RequestMessage?.RequestUri is { } actual && actual != uri) throw new InvalidDataException("资源图标过大或发生重定向。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var bytes = new byte[8192];
        while (true) { var count = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > 524288) throw new InvalidDataException("资源图标过大。"); output.Write(bytes, 0, count); }
        return output.ToArray();
    }
    public static bool AllowedContentIcon(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "media.forgecdn.net" or "mediafilez.forgecdn.net" && uri.AbsolutePath.StartsWith("/avatars/", StringComparison.Ordinal);
}
