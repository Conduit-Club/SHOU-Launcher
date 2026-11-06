namespace SHOU.Launcher.Core.Abstractions;

public sealed record GameCatalogVersion(string Id, string Kind, DateTimeOffset ReleaseTime, bool Latest = false, bool CanInstall = true);
public sealed record GameCatalog(IReadOnlyList<GameCatalogVersion> Versions, string Source, bool Partial = false,
    DateTimeOffset? FetchedAt = null, bool FromCache = false, string? Notice = null);
public interface IMinecraftCatalogCacheService
{
    Task<GameCatalog> RefreshGameCatalogAsync(CancellationToken cancellationToken = default);
}
public sealed record LoaderCatalogVersion(string Version, bool Stable);
public sealed record ContentQuery(string Kind, string Search = "", string? GameVersion = null, string? Loader = null,
    string Sort = "relevance", int Offset = 0);
public sealed record ContentProject(string Id, string Slug, string Title, string Description, string Author,
    long Downloads, string Kind, string? IconUrl, Uri PageUri, string ClientSide = "unknown");
public sealed record ContentFile(string FileName, Uri Uri, string Sha1, long Size, bool Primary = false);
public sealed record ContentRelease(string Id, string Name, string Version, DateTimeOffset Published,
    IReadOnlyList<string> GameVersions, IReadOnlyList<string> Loaders, IReadOnlyList<ContentFile> Files,
    IReadOnlyList<ContentDependency>? Dependencies = null, string? ProjectId = null, string Environment = "unknown");
public sealed record ContentDependency(string Type, string? ProjectId = null, string? VersionId = null, string? FileName = null);
public interface IContentDependencyCatalogService
{
    Task<ContentRelease> GetContentReleaseAsync(string releaseId, CancellationToken cancellationToken = default);
    Task<ContentProject> GetContentProjectAsync(string projectId, CancellationToken cancellationToken = default);
}
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
