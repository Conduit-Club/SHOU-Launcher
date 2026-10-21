using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Conduit;

public sealed class Aria2RpcException(int code) : IOException($"aria2 RPC 返回错误（代码 {code}）。请检查 RPC 密钥、连接及 aria2 日志。")
{
    public int Code { get; } = code;
}

public sealed record Aria2Progress(long Completed, long Total, long Speed, int Connections);

/// <summary>连接外部 aria2。固定 GID 允许重连；仅操作由本启动器创建且路径一致的任务。</summary>
public sealed class Aria2RpcClient(HttpClient client, Uri endpoint, string secret)
{
    public static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("RPC 地址必须是 HTTP(S) URL，且不含凭据、查询参数或片段。");
        return uri;
    }

    public async Task<JsonNode> CallAsync(string method, object[] arguments, CancellationToken cancellationToken = default)
    {
        var parameters = new List<object>();
        if (!string.IsNullOrEmpty(secret)) parameters.Add("token:" + secret);
        parameters.AddRange(arguments);
        var id = Guid.NewGuid().ToString("N");
        var json = System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "aria2." + method, @params = parameters });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))
            ?? throw new IOException("aria2 RPC 未返回 JSON。");
        if ((string?)result["id"] != id || (string?)result["jsonrpc"] != "2.0") throw new IOException("aria2 RPC 响应 ID 或协议版本无效。");
        if (result["error"] is { } error) throw new Aria2RpcException((int?)error["code"] ?? -1);
        response.EnsureSuccessStatusCode();
        return result["result"]?.DeepClone() ?? throw new IOException("aria2 RPC 未返回结果。");
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
        => (string?)(await CallAsync("getVersion", [], cancellationToken).ConfigureAwait(false))["version"] ?? "未知版本";

    public static string TaskId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16].ToLowerInvariant();

    public async Task DownloadAsync(IReadOnlyList<string> urls, string partialPath, string userAgent,
        Action<Aria2Progress>? progress, CancellationToken cancellationToken = default)
    {
        if (urls.Count == 0) throw new ArgumentException("未提供 aria2 下载地址。");
        var fullPath = Path.GetFullPath(partialPath);
        var gid = TaskId(fullPath);
        var attached = false;
        try
        {
            JsonNode? existing = null;
            try { existing = await CallAsync("tellStatus", [gid], cancellationToken).ConfigureAwait(false); }
            catch (Aria2RpcException ex) when (ex.Code == 1) { /* GID not found: add a new resumable job. */ }
            if (existing != null)
            {
                var actual = (string?)existing["files"]?[0]?["path"];
                if (actual == null || !string.Equals(Path.GetFullPath(actual), fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("aria2 任务的输出路径不匹配，拒绝接管。");
                attached = true;
                var status = (string?)existing["status"];
                if (status is "error" or "removed" || (status == "complete" && !File.Exists(fullPath)))
                {
                    await CallAsync("removeDownloadResult", [gid], cancellationToken).ConfigureAwait(false);
                    existing = null;
                }
                else if (status == "paused")
                    await CallAsync("unpause", [gid], cancellationToken).ConfigureAwait(false);
            }
            if (existing == null)
            {
                var options = new Dictionary<string, string>
                {
                    ["gid"] = gid, ["dir"] = Path.GetDirectoryName(fullPath)!, ["out"] = Path.GetFileName(fullPath),
                    ["continue"] = "true", ["auto-file-renaming"] = "false", ["allow-overwrite"] = "true",
                    ["file-allocation"] = "none", ["uri-selector"] = "inorder", ["split"] = "1",
                    ["max-connection-per-server"] = "1", ["max-tries"] = "1", ["connect-timeout"] = "15", ["timeout"] = "30"
                };
                if (!string.IsNullOrWhiteSpace(userAgent)) options["user-agent"] = userAgent;
                attached = true;
                await CallAsync("addUri", [urls.ToArray(), options], cancellationToken).ConfigureAwait(false);
            }
            attached = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await CallAsync("tellStatus", [gid], cancellationToken).ConfigureAwait(false);
                long Number(string key) => long.TryParse((string?)status[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                progress?.Invoke(new(Number("completedLength"), Number("totalLength"), Number("downloadSpeed"), (int)Number("connections")));
                switch ((string?)status["status"])
                {
                    case "complete":
                        if (!File.Exists(fullPath)) throw new IOException("aria2 已完成，但本机找不到下载文件。远程 aria2 需要可共享的相同输出路径。");
                        return;
                    case "error":
                    case "removed": throw new IOException($"aria2 下载失败（代码 {(string?)status["errorCode"] ?? "未知"}）。分片已保留，可重试续传。");
                    case "paused": throw new IOException("aria2 任务已在外部暂停，请重试以继续。");
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (attached)
            {
                using var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await CallAsync("forcePause", [gid], pauseTimeout.Token).ConfigureAwait(false); }
                catch (Exception) { /* RPC may be unavailable; leave the job and its files available for reconnect. */ }
            }
            throw;
        }
    }

    public async Task ForgetCompletedAsync(string partialPath, CancellationToken cancellationToken = default)
    {
        await CallAsync("removeDownloadResult", [TaskId(partialPath)], cancellationToken).ConfigureAwait(false);
    }
}
