namespace SHOU.Launcher.Core.Abstractions;

public enum LocalPackFormat { Unknown, Modrinth, MultiMC, CurseForge, ShouExport }

/// <summary>Detected archive contents, never inferred from the file extension. Unsupported formats carry a reason.</summary>
public sealed record LocalPackInspection(LocalPackFormat Format, string Name, string GameVersion, string Loader,
    string? LoaderVersion, string ArchiveSha256, IReadOnlyList<string> Warnings, bool CanImport,
    string? CapabilityReason = null, ModrinthPackInspection? Modrinth = null,
    IReadOnlyList<string>? LocalFiles = null, int SkippedFiles = 0);
public sealed record LocalPackImportRequest(string ArchivePath, string ExpectedSha256, string InstanceName,
    IReadOnlyList<string>? OptionalClientFiles = null);
public sealed record LocalPackImportResult(GameInstance Instance, int InstalledFiles, IReadOnlyList<string> Warnings,
    ModrinthPackImportResult? Modrinth = null);

public interface ILocalPackService
{
    Task<LocalPackInspection> InspectLocalPackAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request, CancellationToken cancellationToken = default);
}
