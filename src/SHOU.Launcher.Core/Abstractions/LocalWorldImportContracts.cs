namespace SHOU.Launcher.Core.Abstractions;

public sealed record LocalWorldImportRequest(string InstanceId, string ArchivePath, string WorldDirectoryName);
public sealed record LocalWorldImportPreview(WorldContentPreview Installation, string WorldName, string ArchiveFileName, string ArchiveSha256);

/// <summary>Inspect a local Java world without changing saves; explicitly publish to a new world directory.</summary>
public interface ILocalWorldImportService
{
    Task<LocalWorldImportPreview> PreviewLocalWorldImportAsync(LocalWorldImportRequest request, CancellationToken cancellationToken = default);
    Task<WorldContentResult> ImportLocalWorldAsync(string previewId, CancellationToken cancellationToken = default);
    Task DiscardLocalWorldImportAsync(string previewId, CancellationToken cancellationToken = default);
}
