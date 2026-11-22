using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tools;

/// <summary>Anonymous user-requested downloads, separate from authenticated API transports. Never executes files.</summary>
public static class ToolFileTransfer
{
    public const long MaximumBytes = 512L * 1024 * 1024;
    public static Uri ValidateSource(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 8192)
            throw new ArgumentException("请输入 HTTP 或 HTTPS 下载地址，不要包含账户密码或片段。");
        return uri;
    }
    public static string ValidateDestination(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择本机保存位置。");
        var destination = Path.GetFullPath(path); var name = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 200 || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("请输入有效的文件名。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9') throw new ArgumentException("文件名不能使用系统设备名称。");
        VerifiedDownloader.RejectLink(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("保存位置已存在文件，未覆盖；请更换文件名。");
        return destination;
    }
    public static async Task DownloadAsync(HttpClient http, Uri uri, string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        uri = ValidateSource(uri); var destination = ValidateDestination(path);
        var directory = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(directory); VerifiedDownloader.RejectLink(destination);
        var stage = Path.Combine(directory, ".shou-tool-" + Guid.NewGuid().ToString("N") + ".part"); var owned = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5)); var token = timeout.Token;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } final && final != uri) throw new InvalidDataException("下载连接发生未审核的跳转，请重试。");
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (hop == 5 || response.Headers.Location is not { } location) throw new InvalidDataException("下载重定向次数过多或地址无效。");
                    var next = ValidateSource(new Uri(uri, location));
                    if (uri.Scheme == "https" && next.Scheme != "https") throw new InvalidDataException("下载服务器跳转到不安全的连接。");
                    uri = next; continue;
                }
                if (!response.IsSuccessStatusCode) throw new IOException($"下载失败（HTTP {(int)response.StatusCode}）。");
                var total = response.Content.Headers.ContentLength;
                if (total is > MaximumBytes) throw new InvalidDataException("自定义文件超过 512 MiB 限制。");
                VerifiedDownloader.RejectLink(stage);
                await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    owned = true; await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var buffer = new byte[65536]; long count = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
                        count += read; if (count > MaximumBytes || total is { } expected && count > expected) throw new InvalidDataException("下载大小超出限制或服务器声明。");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        progress?.Report(new(destination, "Downloading", count, total, uri.Host));
                    }
                    if (total is { } length && count != length) throw new InvalidDataException("下载不完整，请重试。");
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested(); ValidateDestination(destination); VerifiedDownloader.RejectLink(stage);
                File.Move(stage, destination, false); owned = false; return;
            }
        }
        catch (HttpRequestException) { throw new IOException("无法连接下载服务器，请检查网络后重试。"); }
        finally { if (owned) { VerifiedDownloader.RejectLink(stage); File.Delete(stage); } }
    }
}
