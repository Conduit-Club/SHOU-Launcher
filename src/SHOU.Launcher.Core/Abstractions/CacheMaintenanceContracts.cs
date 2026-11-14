namespace SHOU.Launcher.Core.Abstractions;

public sealed record CacheFile(string Area, string RelativePath, long Bytes, string Sha256);
public sealed record CacheCleanupPlan(IReadOnlyList<CacheFile> Files, int SkippedFiles)
{
    public long Bytes => Files.Sum(f => f.Bytes);
}
public sealed record CacheCleanupResult(int DeletedFiles, long FreedBytes, int SkippedFiles, bool Cancelled);
public interface ICacheMaintenanceService
{
    Task<CacheCleanupPlan> ScanLauncherCacheAsync(CancellationToken cancellationToken = default);
    Task<CacheCleanupResult> ClearLauncherCacheAsync(CacheCleanupPlan plan, CancellationToken cancellationToken = default);
}
