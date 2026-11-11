namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstanceModUpdate(string Token, string InstanceId, string RelativePath, string Name,
    string CurrentVersion, string NewVersion, long DownloadBytes);
public sealed record InstanceModUpdateCheck(IReadOnlyList<InstanceModUpdate> Updates, int CheckedFiles, IReadOnlyList<string> Notices);
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
