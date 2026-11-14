using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceModManagementService
{
    public Task SetInstanceModsEnabledAsync(string instanceId, IReadOnlyList<string> paths, bool enabled, CancellationToken cancellationToken = default)
        => ExecuteAsync("Ready", ct => { var instance = ContentTarget(instanceId, true); ModFileManagement.Toggle(instance, paths, enabled, () => EnsureInstanceNotRunning(instance), ct); return Task.CompletedTask; }, cancellationToken);
    public async Task<InstanceModTrash> TrashInstanceModsAsync(string instanceId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        InstanceModTrash? result = null;
        await ExecuteAsync("Ready", async ct => { var instance = ContentTarget(instanceId, true); result = await ModFileManagement.TrashAsync(instance, paths, () => EnsureInstanceNotRunning(instance), ct); }, cancellationToken);
        return result!;
    }
    public async Task<IReadOnlyList<InstanceModTrash>> GetInstanceModTrashAsync(string instanceId, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken); return await ModFileManagement.ListAsync(ContentTarget(instanceId, false), cancellationToken); }
    public Task RestoreInstanceModsAsync(string instanceId, string trashId, CancellationToken cancellationToken = default)
        => ExecuteAsync("Ready", async ct => { var instance = ContentTarget(instanceId, true); await ModFileManagement.RestoreAsync(instance, trashId, () => EnsureInstanceNotRunning(instance), ct); }, cancellationToken);
}
