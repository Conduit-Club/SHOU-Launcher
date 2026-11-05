namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstancePresentationEdit(string Icon, string Category, string Description, bool Favorite);
public interface IInstancePresentationService
{
    Task UpdateInstancePresentationAsync(string instanceId, InstancePresentationEdit presentation, CancellationToken cancellationToken = default);
}
