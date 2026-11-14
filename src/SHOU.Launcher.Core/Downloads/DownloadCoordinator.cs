using System.Text;
using System.Collections.Concurrent;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Optional external RPC, with verified local output and internal fallback.</summary>
public sealed class DownloadCoordinator(HttpClient http, IVerifiedDownloader internalDownloader, NetworkOptions options,
    Action<string>? onWarning = null, bool policyHandledByTransport = false) : IVerifiedDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var configuration = options.Capture();
        // Catalogs without a checksum must be fetched fresh; do not hand them to a resumable daemon.
        var reason = !configuration.Aria2.Enabled ? null : request.Sha1 is null && request.Sha256 is null ? "无哈希目录使用内置下载器" :
            !policyHandledByTransport && configuration.Downloads is { MaximumKiBPerSecond: > 0 } ? "全局限速由内置下载器统一执行" :
            !policyHandledByTransport && configuration.Downloads?.HttpsOnly == true ? "严格 HTTPS 策略由内置下载器执行" : null;
        IProgress<TransferProgress> InternalProgress(string? detail) => new CallbackProgress<TransferProgress>(p =>
            progress?.Report(p with { Backend = "内置", Detail = p.Detail ?? detail }));
        if (!configuration.Aria2.Enabled || reason is not null)
        { await internalDownloader.DownloadAsync(request, InternalProgress(reason), cancellationToken).ConfigureAwait(false); return; }
        if (request.Sources.Count == 0 || request.Sources.Any(uri => uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("Only HTTP(S) downloads without URL credentials are supported.");
        var destination = Path.GetFullPath(request.DestinationPath);
        var rpcStaging = destination + ".aria2-part";
        var leasePath = destination + ".coordinator.lock";
        void CheckPaths()
        {
            foreach (var path in new[] { destination, rpcStaging, rpcStaging + ".aria2", leasePath }) VerifiedDownloader.RejectLink(path);
        }
        CheckPaths();
        var gate = Locks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CheckPaths();
            await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await VerifiedDownloader.IsValidAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false))
            { progress?.Report(new(destination, "Completed", new FileInfo(destination).Length, request.Size, "cache", "缓存")); return; }
            if (await LocalDownloadReuse.TryImportAsync(request, progress, cancellationToken).ConfigureAwait(false)) return;
            try
            {
                progress?.Report(new(destination, "Queued", 0, request.Size, "aria2", "aria2", "等待全局下载名额"));
                var maximum = Math.Clamp(configuration.Downloads?.MaximumConnections ?? 8, 1, 64);
                var connections = request.Size is >= 16 * 1024 * 1024 ? Math.Min(4, maximum) : 1;
                using var budget = await options.Budget.EnterAsync(maximum, connections, cancellationToken).ConfigureAwait(false);
                var rpc = await options.Aria2Connection.ConnectAsync(options, configuration, cancellationToken).ConfigureAwait(false);
                if (rpc is null) throw new IOException("本机 Aria2 RPC 未连接。");
                var transportOptions = await ProxyOptionsAsync(request.Sources, configuration, cancellationToken).ConfigureAwait(false);
                await rpc.DownloadAsync(request.Sources.Select(s => s.AbsoluteUri).ToArray(), rpcStaging, "SHOU-Launcher/1.6.1",
                    value => progress?.Report(new(request.DestinationPath, "Downloading", value.Completed, value.Total, "aria2", "aria2", $"{value.Connections} 个连接 · {value.Speed / 1024d:0.0} KiB/s", value.Connections)), cancellationToken, connections, transportOptions).ConfigureAwait(false);
                progress?.Report(new(destination, "Verifying", new FileInfo(rpcStaging).Length, request.Size, "aria2", "aria2", "本机校验大小与哈希"));
                if (!await VerifiedDownloader.IsValidAsync(rpcStaging, request, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("aria2 output failed local size/hash verification.");
                CheckPaths();
                File.Move(rpcStaging, destination, true);
                await rpc.ForgetCompletedAsync(rpcStaging, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(request.DestinationPath, "Completed", new FileInfo(request.DestinationPath).Length, request.Size, "aria2", "aria2"));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or CredentialStoreException or System.Text.Json.JsonException or ArgumentException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (ex is HttpRequestException or OperationCanceledException or Aria2RpcException)
                    await options.Aria2Connection.MarkUnavailableAsync(configuration.Revision).ConfigureAwait(false);
                onWarning?.Invoke("aria2不可用或本机输出验证失败，使用内部下载器；原因类型：" + (ex is Aria2RpcException failure ? $"RPC {failure.Code} / {failure.Data["method"]}" : ex.GetType().Name)); }
            await internalDownloader.DownloadAsync(request, InternalProgress("aria2 不可用或产物未通过校验，已回退内置下载器"), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task<IReadOnlyDictionary<string, string>> ProxyOptionsAsync(IReadOnlyList<Uri> sources, NetworkConfiguration configuration, CancellationToken ct)
    {
        var result = new Dictionary<string, string> { ["all-proxy"] = "", ["no-proxy"] = "127.0.0.1,localhost,::1" };
        if (sources.All(s => s.IsLoopback) || configuration.Proxy.Mode == ProxyMode.Direct) return result;
        Uri? proxy = null;
        if (configuration.Proxy.Mode == ProxyMode.Custom) proxy = new Uri(configuration.Proxy.Address!);
        else
        {
            var proxies = sources.Select(s => s.IsLoopback || System.Net.Http.HttpClient.DefaultProxy.IsBypassed(s)
                ? null : System.Net.Http.HttpClient.DefaultProxy.GetProxy(s)).Distinct().ToArray();
            if (proxies.Length > 1) throw new IOException("来源使用不同系统代理规则，转内置传输。");
            proxy = proxies.SingleOrDefault();
        }
        if (proxy is null) return result;
        if (proxy.Scheme is not ("http" or "https")) throw new IOException("系统代理协议转由内置传输执行。");
        result["all-proxy"] = proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (configuration.Proxy.Mode == ProxyMode.Custom && configuration.Proxy.CredentialKey is { Length: > 0 } key)
        {
            var bytes = await (options.Credentials ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "安全存储不可用。")).ReadAsync(key, ct).ConfigureAwait(false)
                ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "代理凭据缺失。");
            try { var credential = Encoding.UTF8.GetString(bytes); var colon = credential.IndexOf(':'); if (colon < 0) throw new IOException("代理凭据格式无效。"); result["all-proxy-user"] = credential[..colon]; result["all-proxy-passwd"] = credential[(colon + 1)..]; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        return result;
    }

}
