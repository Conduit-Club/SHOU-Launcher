namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstancePresentationEdit(string Icon, string Category, string Description, bool Favorite);
public interface IInstancePresentationService
{
    Task UpdateInstancePresentationAsync(string instanceId, InstancePresentationEdit presentation, CancellationToken cancellationToken = default);
}
public interface IInstanceArtworkService
{
    Task<byte[]?> ReadInstanceIconAsync(string instanceId, CancellationToken cancellationToken = default);
    Task SaveInstanceIconAsync(string instanceId, ReadOnlyMemory<byte> squarePng, CancellationToken cancellationToken = default);
}
