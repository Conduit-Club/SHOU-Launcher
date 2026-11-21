using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceContentTrashService
{
    private static void ResourceTrashKind(string kind)
    { if (kind is not ("resourcepacks" or "shaders" or "schematics")) throw new ArgumentException("请选择资源包、光影或投影原理图。"); }
    public async Task<InstanceModTrash> TrashInstanceContentAsync(string instanceId, string kind, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ResourceTrashKind(kind); InstanceModTrash? result = null;
        await ExecuteAsync("Ready", async ct => { var instance = ContentTarget(instanceId, true); result = await ModFileManagement.TrashAsync(instance, paths, () => EnsureInstanceNotRunning(instance), ct, kind); }, cancellationToken);
        return result!;
    }
    public async Task<IReadOnlyList<InstanceModTrash>> GetInstanceContentTrashAsync(string instanceId, string kind, CancellationToken cancellationToken = default)
    { ResourceTrashKind(kind); await InitializeAsync(cancellationToken); return await ModFileManagement.ListAsync(ContentTarget(instanceId, false), cancellationToken, kind); }
    public Task RestoreInstanceContentAsync(string instanceId, string kind, string trashId, CancellationToken cancellationToken = default)
    {
        ResourceTrashKind(kind);
        return ExecuteAsync("Ready", async ct => { var instance = ContentTarget(instanceId, true); await ModFileManagement.RestoreAsync(instance, trashId, () => EnsureInstanceNotRunning(instance), ct, kind); }, cancellationToken);
    }
}
