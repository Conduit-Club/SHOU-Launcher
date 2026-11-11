using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>A repeat launch can inspect immutable cached files, never repair them beneath a live game.</summary>
internal sealed class InstalledFileReader : IVerifiedDownloader
{
    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Sha1 is null && request.Sha256 is null || !await VerifiedDownloader.IsValidAsync(request.DestinationPath, request, cancellationToken).ConfigureAwait(false))
            throw new IOException("运行中的实例缺少完整的已校验基础文件；请先退出此实例的所有游戏窗口，再补全文件。");
        progress?.Report(new(request.DestinationPath, "Completed", new FileInfo(request.DestinationPath).Length, request.Size, "cache"));
    }
}
