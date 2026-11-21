namespace SHOU.Launcher.Core.Abstractions;

public sealed record ContentInstallRequest(string InstanceId, string ProjectId, string ReleaseId, string FileName, string Kind);
public sealed record ContentInstallFile(string ProjectId, string ReleaseId, string Kind, string RelativePath, ContentFile File, bool Dependency);
public sealed record ContentInstallPlan(string InstanceId, string GameVersion, string Loader,
    IReadOnlyList<ContentInstallFile> Files, IReadOnlyList<string> Warnings);
public sealed record ContentInstalledFile(string ProjectId, string ReleaseId, string Kind, string RelativePath,
    string Sha1, string Sha512, long Size, string? DownloadUrl = null, string Client = "required", string Server = "unsupported");
public sealed record ContentInstallManifest(int FormatVersion, string InstanceId, IReadOnlyList<ContentInstalledFile> Files);
public sealed record ContentInstallResult(string InstanceId, string ManifestPath, IReadOnlyList<ContentInstalledFile> Files,
    int AddedFiles, IReadOnlyList<string> Warnings);
public interface IContentInstallationService
{
    Task<ContentInstallPlan> PlanContentInstallAsync(ContentInstallRequest request, CancellationToken cancellationToken = default);
    Task<ContentInstallResult> InstallContentAsync(ContentInstallRequest request, CancellationToken cancellationToken = default);
}
