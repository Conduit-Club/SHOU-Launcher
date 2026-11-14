namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Directory is relative to the selected category, never to the game root.</summary>
public interface IInstanceContentBrowserService
{
    Task<IReadOnlyList<InstanceContentEntry>> BrowseInstanceContentAsync(string instanceId, string kind, string directory, CancellationToken cancellationToken = default);
    Task<string> GetInstanceContentDirectoryAsync(string instanceId, string kind, string directory, CancellationToken cancellationToken = default);
    Task ImportInstanceContentToDirectoryAsync(string instanceId, string kind, string directory, string sourcePath, CancellationToken cancellationToken = default);
}
