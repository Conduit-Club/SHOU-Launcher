namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstanceModUpdate(string Token, string InstanceId, string RelativePath, string Name,
    string CurrentVersion, string NewVersion, long DownloadBytes, Uri? ChangelogUri = null, string? ProjectId = null);
public sealed record InstanceModOnlineInfo(string RelativePath, ContentProject Project, string? Version = null);
public sealed record InstanceModUpdateCheck(IReadOnlyList<InstanceModUpdate> Updates, int CheckedFiles, IReadOnlyList<string> Notices, IReadOnlyList<InstanceModOnlineInfo>? OnlineInfo = null);
public sealed record InstanceModUpdateResult(string BackupDirectory, int InstalledFiles);
public interface IInstanceModUpdateService
{
    Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<InstanceModUpdateResult> ApplyInstanceModUpdateAsync(string token, CancellationToken cancellationToken = default);
}

public interface IInstanceModUpdateProgressService
{
    Task<InstanceModUpdateCheck> CheckInstanceModUpdatesWithProgressAsync(string instanceId, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default);
}
public interface IInstanceModRefreshService
{
    Task<InstanceModUpdateCheck> RefreshInstanceModUpdatesAsync(string instanceId, IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record InstanceModTrash(string Id, int Files, DateTimeOffset Created);
public interface IInstanceModManagementService
{
    Task SetInstanceModsEnabledAsync(string instanceId, IReadOnlyList<string> paths, bool enabled, CancellationToken cancellationToken = default);
    Task<InstanceModTrash> TrashInstanceModsAsync(string instanceId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceModTrash>> GetInstanceModTrashAsync(string instanceId, CancellationToken cancellationToken = default);
    Task RestoreInstanceModsAsync(string instanceId, string trashId, CancellationToken cancellationToken = default);
}
