using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>Caller holds the destination download lease. Original game files are read-only.</summary>
internal static class LocalDownloadReuse
{
    internal static async Task<bool> TryImportAsync(DownloadRequest request, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (request.LocalSources is not { Count: > 0 } sources || sources.Count > 64
            || request.Sha1 is null && request.Sha256 is null) return false;
        foreach (var candidate in sources)
        {
            ct.ThrowIfCancellationRequested(); string? temporary = null;
            try
            {
                VerifiedDownloader.RejectLink(candidate.Root);
                var source = SafePath.Within(candidate.Root, candidate.RelativePath);
                VerifiedDownloader.RejectLink(source);
                if (!File.Exists(source)) continue;
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (request.Size is { } size && input.Length != size) continue;
                VerifiedDownloader.RejectLink(request.DestinationPath);
                temporary = request.DestinationPath + ".reuse-" + Guid.NewGuid().ToString("N") + ".tmp";
                progress?.Report(new(request.DestinationPath, "Verifying", 0, request.Size, "cache", "缓存", "校验旧目录中的已有文件"));
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (!await VerifiedDownloader.IsValidAsync(temporary, request, ct).ConfigureAwait(false)) continue;
                ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(request.DestinationPath);
                File.Move(temporary, request.DestinationPath, true); temporary = null;
                progress?.Report(new(request.DestinationPath, "Completed", input.Length, request.Size, "cache", "缓存", "已复用旧目录文件"));
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { /* An unavailable or unverifiable old file falls back to the verified network transport. */ }
            finally
            {
                if (temporary is not null) { VerifiedDownloader.RejectLink(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return false;
    }
}
