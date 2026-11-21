using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IWorldDatapackTrashService
{
    public async Task<IReadOnlyList<WorldDatapackTrashEntry>> GetWorldDatapackTrashAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await WorldStore(instanceId, false).GetDatapackTrashAsync(worldDirectoryName, cancellationToken).ConfigureAwait(false);
    }
    public Task RecycleWorldDatapacksAsync(string instanceId, string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackSelection> packs, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Ready", ct => WorldStore(instanceId, true).RecycleDatapacksAsync(worldDirectoryName, expectedRevision, packs, ct), cancellationToken);
    public Task RestoreWorldDatapacksAsync(string instanceId, string worldDirectoryName, string trashId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("Ready", ct => WorldStore(instanceId, true).RestoreDatapacksAsync(worldDirectoryName, trashId, ct), cancellationToken);
}
