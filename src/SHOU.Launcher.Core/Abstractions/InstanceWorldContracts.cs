namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Explicit current-instance file actions. No account credentials or level.dat enable-state writes.</summary>
public interface IInstanceWorldService
{
    Task<InstanceServerList> GetInstanceServersAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<InstanceServerList> SaveInstanceServersAsync(string instanceId, string expectedRevision,
        IReadOnlyList<InstanceServerEdit> entries, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceWorldSummary>> GetInstanceWorldsAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorldDatapackFile>> GetWorldDatapacksAsync(string instanceId, string worldDirectoryName,
        CancellationToken cancellationToken = default);
    Task<WorldDatapackFile> ImportWorldDatapackAsync(string instanceId, string worldDirectoryName, string zipPath,
        CancellationToken cancellationToken = default);
}

// ExistingIndex identifies the row in the revision that was read. Null creates a row; omitted rows are removed.
public sealed record InstanceServerEntry(int ExistingIndex, string Name, string Address);
public sealed record InstanceServerEdit(int? ExistingIndex, string Name, string Address);
public sealed record InstanceServerList(string Revision, IReadOnlyList<InstanceServerEntry> Entries, string? BackupPath = null);
public sealed record InstanceWorldSummary(string DirectoryName, string Name, long? Seed, string? Version,
    int? DataVersion, int? GameMode, bool? Hardcore, DateTimeOffset? LastPlayed, string? Error = null);
public sealed record WorldDatapackFile(string Name, bool IsDirectory, long? Size, DateTimeOffset Modified,
    string Status = "仅文件；版本兼容性未验证，尚未修改世界启停状态");
