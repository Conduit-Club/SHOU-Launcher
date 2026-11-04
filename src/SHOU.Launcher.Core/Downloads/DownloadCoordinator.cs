using System.Text;
using System.Collections.Concurrent;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Optional external RPC, with verified local output and internal fallback.</summary>
public sealed class DownloadCoordinator(HttpClient http, IVerifiedDownloader internalDownloader, NetworkOptions options,
    Action<string>? onWarning = null) : IVerifiedDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var configuration = options.Capture();
        // Catalogs without a checksum must be fetched fresh; do not hand them to a resumable daemon.
        if (!configuration.Aria2.Enabled || request.Sha1 is null && request.Sha256 is null)
        { await internalDownloader.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false); return; }
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
            if (await VerifiedDownloader.IsValidAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false)) return;
            try
            {
                var endpoint = Aria2RpcClient.ParseEndpoint(configuration.Aria2.Endpoint);
                if (!endpoint.IsLoopback) throw new IOException("External aria2 is not local; shared output-path compatibility has not been verified.");
                var secret = "";
                if (configuration.Aria2.CredentialKey is { Length: > 0 } key)
                {
                    var bytes = await (options.Credentials ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "Credential store unavailable.")).ReadAsync(key, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) throw new CredentialStoreException(CredentialFailure.Unavailable, "aria2 RPC credential is missing.");
                    secret = Encoding.UTF8.GetString(bytes); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.DestinationPath))!);
                var rpc = new Aria2RpcClient(http, endpoint, secret);
                await rpc.DownloadAsync(request.Sources.Select(s => s.AbsoluteUri).ToArray(), rpcStaging, "SHOU-Launcher/1.5.0",
                    value => progress?.Report(new(request.DestinationPath, "Downloading", value.Completed, value.Total, "aria2")), cancellationToken).ConfigureAwait(false);
                if (!await VerifiedDownloader.IsValidAsync(rpcStaging, request, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("aria2 output failed local size/hash verification.");
                CheckPaths();
                File.Move(rpcStaging, destination, true);
                await rpc.ForgetCompletedAsync(rpcStaging, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(request.DestinationPath, "Completed", new FileInfo(request.DestinationPath).Length, request.Size, "aria2"));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or CredentialStoreException or System.Text.Json.JsonException or ArgumentException)
            { onWarning?.Invoke("aria2不可用或本机输出验证失败，使用内部下载器；原因类型：" + ex.GetType().Name); }
            await internalDownloader.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
