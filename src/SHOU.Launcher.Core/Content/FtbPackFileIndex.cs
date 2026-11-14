using System.Net.Http.Headers;
using System.Text.Json;

namespace SHOU.Launcher.Core.Content;

public sealed record FtbPackIdentity(string Name, string Version, string GameVersion, string Loader, string? LoaderVersion);
internal sealed record FtbPublishedFile(int ProjectId, int FileId, string Folder, string Sha1, string Sha512, long Size, Uri Uri);

/// <summary>Only public, exact-version FTB manifests supply mirrors; no CDN URL is reconstructed.</summary>
internal static class FtbPackFileIndex
{
    internal static bool AllowedUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host is "files.feed-the-beast.com" or "cdn.feed-the-beast.com";
    internal static async Task<IReadOnlyDictionary<(int, int), FtbPublishedFile>> ReadAsync(HttpClient http, FtbPackIdentity? identity, CancellationToken ct)
    {
        var files = new Dictionary<(int, int), FtbPublishedFile>();
        if (identity is null || !identity.Name.StartsWith("FTB ", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(identity.Version)) return files;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var search = await JsonAsync(http, "search/10?term=" + Uri.EscapeDataString(identity.Name), 1024 * 1024, timeout.Token).ConfigureAwait(false);
            var packs = search.RootElement.GetProperty("packs");
            if (packs.ValueKind != JsonValueKind.Array || packs.GetArrayLength() > 10) return files;
            foreach (var id in packs.EnumerateArray())
            {
                if (!id.TryGetInt32(out var packId) || packId <= 0) continue;
                using var pack = await JsonAsync(http, packId.ToString(System.Globalization.CultureInfo.InvariantCulture), 1024 * 1024, timeout.Token).ConfigureAwait(false);
                var root = pack.RootElement;
                if (root.GetProperty("id").GetInt32() != packId || root.GetProperty("private").ValueKind != JsonValueKind.False ||
                    !string.Equals(root.GetProperty("name").GetString(), identity.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var versions = root.GetProperty("versions"); if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() > 2048) return files;
                var matches = versions.EnumerateArray().Where(v => v.GetProperty("private").ValueKind == JsonValueKind.False && v.GetProperty("name").GetString() == identity.Version).ToArray();
                if (matches.Length != 1 || !matches[0].GetProperty("id").TryGetInt32(out var versionId) || versionId <= 0) return files;
                using var manifest = await JsonAsync(http, packId + "/" + versionId, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                var version = manifest.RootElement;
                if (version.GetProperty("private").ValueKind != JsonValueKind.False || version.GetProperty("id").GetInt32() != versionId ||
                    version.GetProperty("parent").GetInt32() != packId || version.GetProperty("name").GetString() != identity.Version) return files;
                var targets = version.GetProperty("targets").EnumerateArray().ToArray();
                if (!targets.Any(t => t.GetProperty("name").GetString() == "minecraft" && t.GetProperty("version").GetString() == identity.GameVersion) ||
                    identity.Loader != "vanilla" && !targets.Any(t => t.GetProperty("name").GetString() == identity.Loader && t.GetProperty("version").GetString() == identity.LoaderVersion)) return files;
                var entries = version.GetProperty("files");
                if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > ModrinthPackArchive.MaximumEntries) return files;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("curseforge", out var cf) || cf.ValueKind != JsonValueKind.Object ||
                        !int.TryParse(cf.GetProperty("project").ToString(), out var project) || project <= 0 || !int.TryParse(cf.GetProperty("file").ToString(), out var file) || file <= 0) continue;
                    if (entry.TryGetProperty("serveronly", out var server) && server.ValueKind == JsonValueKind.True) continue;
                    var folder = entry.GetProperty("path").GetString();
                    if (folder is not ("./mods" or "./resourcepacks" or "./shaderpacks" or "./datapacks")) continue;
                    var sha1 = entry.GetProperty("sha1").GetString(); var hashes = entry.GetProperty("hashes"); var sha512 = hashes.GetProperty("sha512").GetString();
                    if (!ContentInstallFiles.Hash(sha1, 40) || !ContentInstallFiles.Hash(sha512, 128) || !string.Equals(hashes.GetProperty("sha1").GetString(), sha1, StringComparison.OrdinalIgnoreCase) ||
                        !entry.GetProperty("size").TryGetInt64(out var size) || size is <= 0 or > PackArchiveReader.MaximumFileBytes ||
                        !Uri.TryCreate(entry.GetProperty("url").GetString(), UriKind.Absolute, out var uri) || !(AllowedUri(uri) || CurseForgePackResolver.AllowedCurseForgeUri(uri))) continue;
                    if (!files.TryAdd((project, file), new(project, file, folder[2..] + "/", sha1!.ToLowerInvariant(), sha512!.ToLowerInvariant(), size, uri)))
                        throw new InvalidDataException("FTB 官方清单含重复文件身份。");
                }
                return files;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Dictionary<(int, int), FtbPublishedFile>(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new Dictionary<(int, int), FtbPublishedFile>(); }
        ct.ThrowIfCancellationRequested(); return files;
    }
    private static async Task<JsonDocument> JsonAsync(HttpClient http, string route, int budget, CancellationToken ct)
    {
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("x-api-key") || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("FTB 公共清单请求不能携带全局凭据。");
        var uri = new Uri("https://api.feed-the-beast.com/v1/modpacks/public/modpack/" + route);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.6.0"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } final && final != uri || response.Content.Headers.ContentLength > budget)
            throw new IOException("FTB 公共清单未返回可核验的响应。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[32768];
        while (true) { var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > budget) throw new IOException("FTB 公共清单超过大小预算。"); output.Write(buffer, 0, count); }
        return PackArchiveReader.Json(output.ToArray());
    }
}
