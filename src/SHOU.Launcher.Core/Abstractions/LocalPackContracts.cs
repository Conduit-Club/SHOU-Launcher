namespace SHOU.Launcher.Core.Abstractions;

public enum LocalPackFormat { Unknown, Modrinth, MultiMC, CurseForge, ShouExport }

/// <summary>Detected archive contents, never inferred from the file extension. Unsupported formats carry a reason.</summary>
public sealed record LocalPackInspection(LocalPackFormat Format, string Name, string GameVersion, string Loader,
    string? LoaderVersion, string ArchiveSha256, IReadOnlyList<string> Warnings, bool CanImport,
    string? CapabilityReason = null, ModrinthPackInspection? Modrinth = null,
    IReadOnlyList<string>? LocalFiles = null, int SkippedFiles = 0, string? PackVersion = null, int? RecommendedMemoryMiB = null,
    IReadOnlyList<CurseForgeManualFile>? ManualFiles = null, IReadOnlyList<LocalPackOptionalFile>? OptionalFiles = null,
    IReadOnlyList<string>? SelectedOptionalFiles = null);
public sealed record LocalPackOptionalFile(string Key, string Name, Uri PageUri, string? Notice = null);
public interface ILocalPackSelectionService
{
    Task<LocalPackInspection> InspectLocalPackSelectionAsync(string archivePath, IReadOnlyList<string> optionalFiles,
        IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default, string? expectedArchiveSha256 = null);
    Task ProvideSelectedLocalPackFileAsync(string archivePath, string expectedArchiveSha256, IReadOnlyList<string> optionalFiles,
        int projectId, int fileId, string sourcePath, CancellationToken cancellationToken = default);
}
public sealed record CurseForgeManualFile(int ProjectId, int FileId, string FileName, string Sha1, long Size, Uri PageUri);
public interface ILocalPackManualFileService
{
    Task ProvideLocalPackFileAsync(string archivePath, string expectedArchiveSha256, int projectId, int fileId,
        string sourcePath, CancellationToken cancellationToken = default);
}
public sealed record LocalPackImportRequest(string ArchivePath, string ExpectedSha256, string InstanceName,
    IReadOnlyList<string>? OptionalClientFiles = null, bool PrepareAfterImport = false);
public sealed record LocalPackImportResult(GameInstance Instance, int InstalledFiles, IReadOnlyList<string> Warnings,
    ModrinthPackImportResult? Modrinth = null);

public interface ILocalPackService
{
    Task<LocalPackInspection> InspectLocalPackAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request, CancellationToken cancellationToken = default);
}
