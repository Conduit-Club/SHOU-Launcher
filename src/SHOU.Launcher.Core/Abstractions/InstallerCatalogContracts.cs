namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstallerVersion(string Loader, string GameVersion, string Version, string ArtifactVersion,
    bool Recommended = false, DateTimeOffset? Published = null, bool Latest = false, string? Channel = null);
public sealed record InstallerCatalog(IReadOnlyList<InstallerVersion> Versions, string? Notice = null);
public interface IInstallerCatalogService
{
    Task<InstallerCatalog> GetInstallerCatalogAsync(string loader, CancellationToken cancellationToken = default);
    Task SaveInstallerAsync(InstallerVersion version, string destination, CancellationToken cancellationToken = default);
}
