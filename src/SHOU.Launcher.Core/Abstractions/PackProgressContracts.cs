namespace SHOU.Launcher.Core.Abstractions;

public sealed record PackInstallProgress(string Stage, int CompletedFiles, int TotalFiles, string? CurrentFile = null);

public interface ILocalPackProgressService
{
    Task<LocalPackInspection> InspectLocalPackWithProgressAsync(string archivePath, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default);
    Task<LocalPackImportResult> ImportLocalPackWithProgressAsync(LocalPackImportRequest request, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default);
}

public interface IInstanceInstallProgressService
{
    Task InstallInstanceWithProgressAsync(string instanceId, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default);
}
