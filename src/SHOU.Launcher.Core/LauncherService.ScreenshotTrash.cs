using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IScreenshotTrashService
{
    public async Task<IReadOnlyList<ScreenshotTrashEntry>> ListScreenshotTrashAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        return await ScreenshotTrash.ListAsync(instance, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ScreenshotTrashEntry> MoveScreenshotToTrashAsync(string instanceId, string relativePath, CancellationToken cancellationToken = default)
    {
        ScreenshotTrashEntry? entry = null;
        await ExecuteAsync("Ready", async ct => { var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory); entry = await ScreenshotTrash.MoveAsync(instance, relativePath, ct).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
        return entry!;
    }
    public Task RestoreScreenshotAsync(string instanceId, string trashId, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    { var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory); await ScreenshotTrash.RestoreAsync(instance, trashId, ct).ConfigureAwait(false); }, cancellationToken);
}
