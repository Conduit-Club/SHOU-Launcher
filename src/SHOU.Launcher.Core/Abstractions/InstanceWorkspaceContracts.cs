namespace SHOU.Launcher.Core.Abstractions;

public interface IServerLaunchService
{
    Task LaunchToServerAsync(string instanceId, string address, CancellationToken cancellationToken = default);
}

public interface IInstanceDirectoryService
{
    Task<string> GetInstanceDirectoryAsync(string instanceId, string? contentKind = null, bool createMissing = false, CancellationToken cancellationToken = default);
}

public sealed record InstanceEdit(string Name, string? JavaExecutable, int? MemoryMiB, GameLaunchOptions? LaunchOptions = null,
    InstanceDirectoryMode? DirectoryMode = null, bool ConfirmDirectoryChange = false);
public sealed record InstanceContentEntry(string Name, string RelativePath, string FullPath, string Kind,
    long Bytes, DateTimeOffset Modified, bool Enabled = true, bool Directory = false,
    string? ModName = null, string? ModVersion = null, byte[]? IconPng = null)
{
    public string DisplayName => ModName ?? Name;
    public string VersionLabel => ModVersion is { Length: > 0 } ? ModVersion : "";
    public bool HasIcon => IconPng is { Length: > 0 };
}
public interface IInstanceWorkspaceService
{
    Task UpdateInstanceAsync(string instanceId, InstanceEdit edit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string instanceId, string kind, CancellationToken cancellationToken = default);
    Task ImportInstanceContentAsync(string instanceId, string kind, string sourcePath, CancellationToken cancellationToken = default);
    Task SetInstanceModEnabledAsync(string instanceId, string relativePath, bool enabled, CancellationToken cancellationToken = default);
    Task<string> ExportInstanceAsync(string instanceId, bool includeWorlds, CancellationToken cancellationToken = default);
}
