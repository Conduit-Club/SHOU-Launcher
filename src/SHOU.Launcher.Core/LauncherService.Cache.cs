using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Tools;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ICacheMaintenanceService
{
    public async Task<CacheCleanupPlan> ScanLauncherCacheAsync(CancellationToken cancellationToken = default)
    {
        CacheCleanupPlan? plan = null;
        await ExecuteAsync("Ready", async ct => { RequireNoToolGame(); plan = await LauncherCacheCleaner.ScanAsync(Paths, ct); }, cancellationToken).ConfigureAwait(false);
        return plan!;
    }
    public async Task<CacheCleanupResult> ClearLauncherCacheAsync(CacheCleanupPlan plan, CancellationToken cancellationToken = default)
    {
        CacheCleanupResult? result = null;
        await ExecuteAsync("Ready", async ct => { RequireNoToolGame(); result = await LauncherCacheCleaner.ClearAsync(Paths, plan, ct); }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
