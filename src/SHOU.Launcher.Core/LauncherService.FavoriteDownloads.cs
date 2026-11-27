using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IContentBatchDownloadService
{
    public async Task<FavoriteDownloadResult> DownloadFavoriteFilesAsync(IReadOnlyList<FavoriteDownloadEntry> files, string directory, CancellationToken cancellationToken = default)
    {
        FavoriteDownloadResult? result = null;
        await ExecuteAsync("Installing", ct => RunContentTransferAsync("favorite-batch:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..16],
            async token => { await DownloadFavoriteFilesAsync(files, directory, token).ConfigureAwait(false); },
            async token => { result = await FavoriteDownloads.ExportAsync(files, directory, DownloadCachedContentFileAsync, token, Math.Min(4, Snapshot.Settings.Downloads?.MaximumConnections ?? 4)).ConfigureAwait(false); if (result.Failures.Count > 0) throw new IOException($"已保存 {result.Added} 个文件，复用 {result.Reused} 个文件。未完成：" + string.Join("；", result.Failures.Select(f => f.Name + "：" + f.Reason))); }, ct), cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
