namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Recoverable deletion of resource packs, shaders and schematics; mod trash remains compatible.</summary>
public interface IInstanceContentTrashService
{
    Task<InstanceModTrash> TrashInstanceContentAsync(string instanceId, string kind, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceModTrash>> GetInstanceContentTrashAsync(string instanceId, string kind, CancellationToken cancellationToken = default);
    Task RestoreInstanceContentAsync(string instanceId, string kind, string trashId, CancellationToken cancellationToken = default);
}
