namespace SHOU.Launcher.Core.Abstractions;

public sealed record GameCatalogVersion(string Id, string Kind, DateTimeOffset ReleaseTime, bool Latest = false, bool CanInstall = true);
public sealed record GameCatalog(IReadOnlyList<GameCatalogVersion> Versions, string Source, bool Partial = false);
public sealed record LoaderCatalogVersion(string Version, bool Stable);
public sealed record ContentQuery(string Kind, string Search = "", string? GameVersion = null, string? Loader = null,
    string Sort = "relevance", int Offset = 0);
public sealed record ContentProject(string Id, string Slug, string Title, string Description, string Author,
    long Downloads, string Kind, string? IconUrl, Uri PageUri);
public sealed record ContentFile(string FileName, Uri Uri, string Sha1, long Size, bool Primary = false);
public sealed record ContentRelease(string Id, string Name, string Version, DateTimeOffset Published,
    IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders, IReadOnlyList<ContentFile> Files);
public interface ILauncherCatalogService
{
    Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null,
        CancellationToken cancellationToken = default);
    Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default);
    Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default);
}
