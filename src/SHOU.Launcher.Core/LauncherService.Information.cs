using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILauncherInformationService
{
    private readonly HttpClient informationHttp;
    private readonly bool ownsInformationHttp;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LauncherUpdateAsset, byte> desktopUpdateAssets = new();
    private const string RepositoryApi = "https://api.github.com/repos/Conduit-Club/SHOU-Launcher/";
    public async Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(await ReadPublicDocumentAsync(new(RepositoryApi + "releases?per_page=20"), 2 * 1024 * 1024, cancellationToken));
        List<LauncherRelease> releases = [];
        foreach (var release in json.RootElement.EnumerateArray().Take(20))
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            LauncherUpdateAsset? package = null;
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var manifest = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "SHOU-Launcher-" + Snapshot.Platform.RuntimeIdentifier + "-release.json");
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                // A legacy Windows-only EXE must never replace this multi-file cross-platform host.
                var extension = ".zip"; // Both native package entry points publish a ZIP with a platform-specific manifest.
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (!name.StartsWith("SHOU-Launcher-" + Snapshot.Platform.RuntimeIdentifier + "-", StringComparison.Ordinal)
                    || !name.EndsWith(extension, StringComparison.Ordinal) || digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                var hash = digest[7..]; var bytes = asset.GetProperty("size").GetInt64();
                var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && bytes is > 0 and <= 1024L * 1024 * 1024 && IsReleaseAsset(uri))
                {
                    var candidate = new LauncherUpdateAsset(name, uri, bytes, hash.ToLowerInvariant());
                    if (await MatchesDesktopManifestAsync(manifest, candidate, cancellationToken)) { package = candidate; desktopUpdateAssets.TryAdd(candidate, 0); }
                }
            }
            var page = new Uri(release.GetProperty("html_url").GetString()!);
            if (!IsRepositoryPage(page)) throw new InvalidDataException("发行页面地址无效。");
            releases.Add(new(release.GetProperty("tag_name").GetString() ?? "", release.GetProperty("name").GetString() ?? "",
                (release.GetProperty("body").GetString() ?? "")[..Math.Min(32768, (release.GetProperty("body").GetString() ?? "").Length)],
                release.GetProperty("published_at").GetDateTimeOffset(), page, release.GetProperty("prerelease").GetBoolean(), package));
        }
        return releases;
    }
    private async Task<bool> MatchesDesktopManifestAsync(JsonElement manifestAsset, LauncherUpdateAsset package, CancellationToken ct)
    {
        if (manifestAsset.ValueKind != JsonValueKind.Object || !manifestAsset.TryGetProperty("browser_download_url", out var location)
            || !Uri.TryCreate(location.GetString(), UriKind.Absolute, out var uri) || !IsReleaseAsset(uri)) return false;
        try
        {
            using var document = JsonDocument.Parse(await ReadPublicDocumentAsync(uri, 65536, ct)); var info = document.RootElement;
            var source = info.GetProperty("sourceSha").GetString(); var item = info.GetProperty("package");
            return info.GetProperty("format").GetString() == "SHOU-Desktop-Release-v1"
                && info.GetProperty("product").GetString() == "SHOU.Launcher.Desktop"
                && info.GetProperty("runtime").GetString() == Snapshot.Platform.RuntimeIdentifier
                && info.GetProperty("selfContained").GetBoolean() && Version.TryParse(info.GetProperty("avalonia").GetString(), out _)
                && source is { Length: 40 } && source.All(Uri.IsHexDigit)
                && item.GetProperty("name").GetString() == package.Name && item.GetProperty("bytes").GetInt64() == package.Bytes
                && string.Equals(item.GetProperty("sha256").GetString(), package.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException or HttpRequestException)
        { return false; } // Notes remain readable when the portable release identity cannot be established.
    }
    public async Task<IReadOnlyList<LauncherFeedback>> GetLauncherFeedbackAsync(CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(await ReadPublicDocumentAsync(new(RepositoryApi + "issues?state=all&per_page=50"), 2 * 1024 * 1024, cancellationToken));
        List<LauncherFeedback> entries = [];
        foreach (var issue in json.RootElement.EnumerateArray().Take(50))
        {
            if (issue.TryGetProperty("pull_request", out _)) continue;
            var page = new Uri(issue.GetProperty("html_url").GetString()!);
            if (!IsRepositoryPage(page)) throw new InvalidDataException("反馈页面地址无效。");
            var text = issue.GetProperty("body").GetString() ?? "";
            entries.Add(new(issue.GetProperty("title").GetString() ?? "", issue.GetProperty("state").GetString() ?? "", page, text[..Math.Min(4096, text.Length)]));
        }
        return entries;
    }
    public async Task<string> DownloadLauncherUpdateAsync(LauncherUpdateAsset asset, CancellationToken cancellationToken = default)
    {
        if (!desktopUpdateAssets.ContainsKey(asset) || !IsReleaseAsset(asset.Download) || !asset.Name.StartsWith("SHOU-Launcher-" + Snapshot.Platform.RuntimeIdentifier + "-", StringComparison.Ordinal)
            || Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(['/', '\\', ':']) >= 0 || asset.Bytes is <= 0 or > 1024L * 1024 * 1024
            || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("更新包身份或校验信息无效。");
        var directory = SafePath.Within(Paths.CacheDirectory, "launcher-updates");
        VerifiedDownloader.RejectLink(directory); Directory.CreateDirectory(directory);
        var path = SafePath.Within(directory, asset.Sha256 + "-" + asset.Name);
        await new VerifiedDownloader(informationHttp, network).DownloadAsync(new([asset.Download], path, Sha256: asset.Sha256, Size: asset.Bytes), cancellationToken: cancellationToken);
        return path;
    }
    public async Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (remote)
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback) throw new ArgumentException("网络主页须为不含凭据的 HTTPS 文本地址。");
            bytes = await ReadPublicDocumentAsync(uri, 262144, cancellationToken);
        }
        else
        {
            if (!Path.IsPathFullyQualified(location) || Path.GetExtension(location).ToLowerInvariant() is not (".txt" or ".md")) throw new ArgumentException("本地主页支持 TXT / Markdown 文件。");
            VerifiedDownloader.RejectLink(location);
            if (new FileInfo(location).Length > 262144) throw new InvalidDataException("主页文件不能超过 256 KiB。");
            bytes = await File.ReadAllBytesAsync(location, cancellationToken);
        }
        return Encoding.UTF8.GetString(bytes);
    }
    private async Task<byte[]> ReadPublicDocumentAsync(Uri uri, int limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.5.0"); request.Headers.Accept.ParseAdd("application/json");
        using var response = await informationHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("响应超过大小限制。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token); using var target = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0) { if (target.Length + count > limit) throw new InvalidDataException("响应超过大小限制。"); target.Write(buffer, 0, count); }
        return target.ToArray();
    }
    private static bool IsReleaseAsset(Uri uri) => uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/Conduit-Club/SHOU-Launcher/releases/download/", StringComparison.Ordinal);
    private static bool IsRepositoryPage(Uri uri) => uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/Conduit-Club/SHOU-Launcher/", StringComparison.Ordinal);
}
