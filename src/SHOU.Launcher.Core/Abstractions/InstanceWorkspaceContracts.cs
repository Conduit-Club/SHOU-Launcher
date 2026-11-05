namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstanceEdit(string Name, string? JavaExecutable, int? MemoryMiB);
public sealed record InstanceContentEntry(string Name, string RelativePath, string FullPath, string Kind,
    long Bytes, DateTimeOffset Modified, bool Enabled = true, bool Directory = false);
public interface IInstanceWorkspaceService
{
    Task UpdateInstanceAsync(string instanceId, InstanceEdit edit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string instanceId, string kind, CancellationToken cancellationToken = default);
    Task ImportInstanceContentAsync(string instanceId, string kind, string sourcePath, CancellationToken cancellationToken = default);
    Task SetInstanceModEnabledAsync(string instanceId, string relativePath, bool enabled, CancellationToken cancellationToken = default);
    Task<string> ExportInstanceAsync(string instanceId, bool includeWorlds, CancellationToken cancellationToken = default);
}
