using System.Text;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Port-based connection only. No executable discovery, child processes or service ownership.</summary>
public sealed class Aria2RpcConnection
{
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private long revision = -1;
    private DateTimeOffset expires;
    private Aria2RpcClient? client;
    private Aria2ConnectionStatus status = new(false, "尚未连接，将使用内置下载器。", "http://localhost:6800/jsonrpc");
    public async Task<Aria2RpcClient?> ConnectAsync(NetworkOptions network, NetworkConfiguration configuration, CancellationToken ct = default, bool force = false)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && revision == configuration.Revision && DateTimeOffset.UtcNow < expires) return client;
            revision = configuration.Revision; client = null;
            var endpointText = configuration.Aria2.Endpoint;
            try
            {
                var endpoint = Aria2RpcClient.ParseEndpoint(endpointText);
                if (!endpoint.IsLoopback) throw new ArgumentException("仅支持本机 RPC。");
                var secret = "";
                if (configuration.Aria2.CredentialKey is { Length: > 0 } key)
                {
                    var bytes = await (network.Credentials ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "安全存储不可用。")).ReadAsync(key, ct).ConfigureAwait(false)
                        ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "未找到 RPC 密钥。");
                    try { secret = Encoding.UTF8.GetString(bytes); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                }
                var rpc = new Aria2RpcClient(LocalHttp, endpoint, secret);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var version = await rpc.GetVersionAsync(timeout.Token).ConfigureAwait(false);
                if (version is "未知版本" or "" || version.Length > 64 || version.Any(char.IsControl)) throw new IOException("RPC 版本响应无效。");
                client = rpc; status = new(true, "已连接 Aria2，下载优先使用此连接。", endpointText, version); expires = DateTimeOffset.UtcNow.AddSeconds(10);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or OperationCanceledException or CredentialStoreException or System.Text.Json.JsonException or InvalidOperationException)
            { status = new(false, "Aria2 未连接或认证未通过，将使用内置下载器。请先启动 RPC 并核对地址、密钥。", endpointText); expires = DateTimeOffset.UtcNow.AddSeconds(3); }
            return client;
        }
        finally { gate.Release(); }
    }
    public async Task<Aria2ConnectionStatus> TestAsync(NetworkOptions network, CancellationToken ct = default)
    { _ = await ConnectAsync(network, network.Capture(), ct, force: true).ConfigureAwait(false); return status; }
    public async Task MarkUnavailableAsync(long failedRevision)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (revision == failedRevision) { client = null; expires = DateTimeOffset.UtcNow.AddSeconds(3); } }
        finally { gate.Release(); }
    }

}
