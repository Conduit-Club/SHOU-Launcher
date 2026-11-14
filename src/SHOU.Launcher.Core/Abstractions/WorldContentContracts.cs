namespace SHOU.Launcher.Core.Abstractions;

public sealed record WorldContentRequest(string InstanceId, string ProjectId, string ReleaseId, string FileName, string Kind,
    string WorldDirectoryName);
public sealed record WorldContentTarget(string DirectoryName, string Name, string? Version, int? DataVersion, string? Error = null)
{
    public string Label => $"{Name} · {Version ?? "未知版本"} · {DirectoryName}";
}
public sealed record WorldContentPreview(string Id, string Kind, string InstanceName, string WorldDirectoryName,
    string GameVersion, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);
public sealed record WorldContentResult(string InstanceId, string WorldDirectoryName, int AddedFiles, string Destination);
public interface IWorldContentInstallationService
{
    Task<IReadOnlyList<WorldContentTarget>> GetWorldContentTargetsAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<WorldContentPreview> PreviewWorldContentAsync(WorldContentRequest request, CancellationToken cancellationToken = default);
    Task<WorldContentResult> InstallWorldContentAsync(string previewId, CancellationToken cancellationToken = default);
}
