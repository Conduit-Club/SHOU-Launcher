using System.Text.Json;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Community;

/// <summary>Reads public club articles as text, with bounded requests and validated offline cache.</summary>
public sealed class ClubUpdatesClient(HttpClient http, string cacheDirectory)
{
    private const int MaximumHtmlBytes = 2 * 1024 * 1024;
    private string Cache(string name)
    { VerifiedDownloader.RejectLink(cacheDirectory); return Path.Combine(Path.GetFullPath(cacheDirectory), name); }
    public async Task<ClubUpdateHistory> GetHistoryAsync(bool refresh, CancellationToken ct)
    {
        var path = Cache("index.json"); var cached = await ReadCacheAsync<ClubUpdateHistory>(path, ct).ConfigureAwait(false);
        if (cached is not null && (cached.Notices is null || cached.Notices.Count is 0 or > 1000 || cached.Notices.Any(n => !Valid(n)) || cached.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5))) cached = null;
        if (!refresh && cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(15)) return cached with { FromCache = true };
        try
        {
            var text = await ReadHtmlAsync(new(ClubUpdateNoticeParser.IndexUrl), ct).ConfigureAwait(false);
            var notices = ClubUpdateNoticeParser.Parse(text).Take(100).ToArray();
            if (notices.Length == 0) throw new InvalidDataException("社团更新目录暂未返回可阅读文章。");
            var result = new ClubUpdateHistory(notices, DateTimeOffset.UtcNow);
            return await TryWriteCacheAsync(path, result, ct).ConfigureAwait(false) ? result
                : result with { Warning = "日志已读取；本地缓存暂时无法保存。" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            if (cached is not null) return cached with { FromCache = true, Warning = "社团网站暂不可用，正在显示本地缓存。" };
            throw new IOException("暂时无法读取社团更新日志，请重试。", ex);
        }
    }
    public async Task<ClubUpdateArticle> GetArticleAsync(ClubUpdateNotice notice, bool refresh, CancellationToken ct)
    {
        if (!Valid(notice)) throw new InvalidDataException("社团文章地址无效。");
        var path = Cache(notice.Id + ".json"); var cached = await ReadCacheAsync<ClubUpdateArticle>(path, ct).ConfigureAwait(false);
        if (cached is not null && (!Valid(cached.Notice) || cached.Notice.Id != notice.Id || cached.Body is null || cached.Body.Length is 0 or > 120000)) cached = null;
        if (!refresh && cached is not null) return cached;
        try
        {
            var body = await ReadHtmlAsync(notice.Url, ct).ConfigureAwait(false);
            var article = ClubUpdateNoticeParser.ParseArticle(body, notice) ?? throw new InvalidDataException("文章正文暂不可用。");
            await TryWriteCacheAsync(path, article, ct).ConfigureAwait(false); return article;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        { if (cached is not null) return cached; throw new IOException("文章正文读取失败，可重试或在社团网站阅读。", ex); }
    }
    private static bool Valid(ClubUpdateNotice? notice) => notice is { Title.Length: > 0 and <= 200, Url: not null }
        && ClubUpdateNoticeParser.TryGetTrustedArticle(notice.Url.AbsoluteUri, out var date, out _) && date == notice.Date;
    private async Task<string> ReadHtmlAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.5.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        // The dedicated client disables automatic redirects: no remote page can send a request off the trusted origin.
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } actual || actual.Scheme != "https" || actual.Host != ClubUpdateNoticeParser.TrustedHost)
            throw new InvalidDataException("社团页面跳转到了未支持的地址。");
        if (response.Content.Headers.ContentLength > MaximumHtmlBytes) throw new InvalidDataException("社团页面超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        { if (output.Length + read > MaximumHtmlBytes) throw new InvalidDataException("社团页面超过大小限制。"); output.Write(buffer, 0, read); }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    private static async Task<T?> ReadCacheAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            VerifiedDownloader.RejectLink(path); if (!File.Exists(path) || new FileInfo(path).Length > MaximumHtmlBytes) return default;
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return default; }
    }
    private static async Task WriteCacheAsync<T>(string path, T value, CancellationToken ct)
    {
        VerifiedDownloader.RejectLink(Path.GetDirectoryName(path)!); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VerifiedDownloader.RejectLink(path); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<bool> TryWriteCacheAsync<T>(string path, T value, CancellationToken ct)
    {
        try { await WriteCacheAsync(path, value, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
