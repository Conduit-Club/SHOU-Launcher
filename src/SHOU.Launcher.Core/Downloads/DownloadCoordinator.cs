using System.Text;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Optional external RPC, with verified local output and internal fallback.</summary>
public sealed class DownloadCoordinator(HttpClient http, IVerifiedDownloader internalDownloader, NetworkOptions options,
    Action<string>? onWarning = null) : IVerifiedDownloader
{
    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!options.Aria2.Enabled) { await internalDownloader.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false); return; }
        if (await VerifiedDownloader.IsValidAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false)) return;
        var rpcStaging = request.DestinationPath + ".aria2-part";
        try
        {
            var endpoint = Aria2RpcClient.ParseEndpoint(options.Aria2.Endpoint);
            if (!endpoint.IsLoopback) throw new IOException("External aria2 is not local; shared output-path compatibility has not been verified.");
            var secret = "";
            if (options.Aria2.CredentialKey is { Length: > 0 } key)
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
            File.Move(rpcStaging, request.DestinationPath, true);
            await rpc.ForgetCompletedAsync(rpcStaging, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(request.DestinationPath, "Completed", new FileInfo(request.DestinationPath).Length, request.Size, "aria2"));
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or OperationCanceledException or CredentialStoreException or System.Text.Json.JsonException)
        { onWarning?.Invoke("aria2不可用或本机输出验证失败，使用内部下载器；原因类型：" + ex.GetType().Name); }
        await internalDownloader.DownloadAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }
}
