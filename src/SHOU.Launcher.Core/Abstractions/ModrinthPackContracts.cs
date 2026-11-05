namespace SHOU.Launcher.Core.Abstractions;

public sealed record ModrinthPackFile(string Path, string Sha1, string Sha512, long Size,
    IReadOnlyList<string> Downloads, string Client = "required", string Server = "required");
public sealed record ModrinthPackInspection(string Name, string VersionId, string GameVersion, string Loader,
    string? LoaderVersion, string ArchiveSha256, IReadOnlyList<ModrinthPackFile> Files,
    IReadOnlyList<string> Overrides, IReadOnlyList<string> Warnings, bool CanImport, string? CapabilityReason = null);
public sealed record ModrinthPackImportRequest(string ArchivePath, string ExpectedSha256, string InstanceName,
    IReadOnlyList<string>? OptionalClientFiles = null);
public sealed record ModrinthPackImportResult(GameInstance Instance, int InstalledFiles, int SkippedServerFiles, int SkippedOptionalFiles);
public sealed record ModrinthPackExportOptions(string Name, string VersionId = "1.0.0",
    IReadOnlyList<string>? OverridePaths = null, bool BundleInstalledFiles = false);
public interface IModrinthPackService
{
    Task<ModrinthPackInspection> InspectModrinthPackAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<ModrinthPackImportResult> ImportModrinthPackAsync(ModrinthPackImportRequest request, CancellationToken cancellationToken = default);
    Task<string> ExportModrinthPackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default);
}
