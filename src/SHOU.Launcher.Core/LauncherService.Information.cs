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
    private const string RepositoryApi = "https://api.github.com/repos/Conduit-Club/SHOU-Launcher/";
    private async Task<IReadOnlyList<LauncherRelease>> ReadReleasesApiAsync(CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await ReadPublicDocumentAsync(new(RepositoryApi + "releases?per_page=20"), 2 * 1024 * 1024, cancellationToken));
        List<LauncherRelease> releases = [];
        foreach (var release in json.RootElement.EnumerateArray().Take(20))
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var page = new Uri(release.GetProperty("html_url").GetString()!);
            if (!IsRepositoryPage(page)) throw new InvalidDataException("发行页面地址无效。");
            releases.Add(new(release.GetProperty("tag_name").GetString() ?? "", release.GetProperty("name").GetString() ?? "",
                (release.GetProperty("body").GetString() ?? "")[..Math.Min(32768, (release.GetProperty("body").GetString() ?? "").Length)],
                release.GetProperty("published_at").GetDateTimeOffset(), page, release.GetProperty("prerelease").GetBoolean()));
        }
        return releases.OrderByDescending(r => r.Published).ToArray();
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
    public async Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (remote)
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback) throw new ArgumentException("网络主页须为不含凭据的 HTTPS 文本地址。");
            bytes = await ReadPublicDocumentAsync(uri, 262144, cancellationToken, "text/plain");
        }
        else
        {
            if (!Path.IsPathFullyQualified(location) || Path.GetExtension(location).ToLowerInvariant() is not (".txt" or ".md" or ".xaml")) throw new ArgumentException("本地主页支持 TXT / Markdown / XAML 文件。");
            SafePath.RejectLinks(location);
            if (new FileInfo(location).Length > 262144) throw new InvalidDataException("主页文件不能超过 256 KiB。");
            bytes = await File.ReadAllBytesAsync(location, cancellationToken);
        }
        return Encoding.UTF8.GetString(bytes);
    }
    private async Task<byte[]> ReadPublicDocumentAsync(Uri uri, int limit, CancellationToken ct, string accept = "application/json")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.6.0"); request.Headers.Accept.ParseAdd(accept);
        using var response = await informationHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("响应超过大小限制。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token); using var target = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, timeout.Token)) != 0) { if (target.Length + count > limit) throw new InvalidDataException("响应超过大小限制。"); target.Write(buffer, 0, count); }
        return target.ToArray();
    }
    private static bool IsRepositoryPage(Uri uri) => uri.Scheme == "https" && uri.Host == "github.com" && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/Conduit-Club/SHOU-Launcher/", StringComparison.Ordinal);
}
