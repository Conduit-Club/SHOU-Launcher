using System.Collections.Concurrent;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILauncherCatalogService, IMinecraftCatalogCacheService, IContentCategoryCatalogService
{
    private readonly CachedGameCatalog gameCatalogCache;
    private readonly ConcurrentDictionary<string, ContentFile> contentTransfers = new(StringComparer.Ordinal);
    private LauncherCatalog Catalog => new(http, Sources);
    public async Task<IReadOnlyList<ContentCategory>> GetContentCategoriesAsync(CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await AllContentCategoriesAsync(cancellationToken).ConfigureAwait(false); }
    public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => ReadGameCatalogAsync(false, cancellationToken);
    public Task<GameCatalog> RefreshGameCatalogAsync(CancellationToken cancellationToken = default) => ReadGameCatalogAsync(true, cancellationToken);
    private async Task<GameCatalog> ReadGameCatalogAsync(bool refresh, CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var configuration = Snapshot.Settings.DownloadSourcesJson + ":" + (Snapshot.Settings.Downloads?.SourcePreference ?? DownloadSourcePreference.Custom) + ":" + (Snapshot.Settings.Downloads?.PreferOfficialCatalog ?? true);
        var sources = Sources();
        return await gameCatalogCache.GetAsync(configuration, refresh, new LauncherCatalog(http, () => sources, Snapshot.Settings.Downloads?.PreferOfficialCatalog ?? true).GamesAsync, ct).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.FabricAsync(gameVersion, cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default)
    { return (await SearchContentPageAsync(query, cancellationToken).ConfigureAwait(false)).Projects; }
    public async Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!CurseForgeCatalog.IsProject(projectId)) return await Catalog.ReleasesAsync(projectId, gameVersion, loader, cancellationToken).ConfigureAwait(false);
        using var transport = ContentTransport();
        return await new CurseForgeCatalog(transport, build.CurseForgeApiKey).ReleasesAsync(projectId, gameVersion, loader, cancellationToken).ConfigureAwait(false);
    }
    public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default)
        => Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri) && AllowedContentIcon(uri) ? CurseForgeIconAsync(uri, cancellationToken) : Catalog.IconAsync(iconUrl, cancellationToken);
    public async Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default)
    {
        if (file.FileName.Length is 0 or > 200 || file.FileName.Contains('/') || file.FileName.Contains('\\') || file.FileName is "." or ".."
            || file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit) || file.Size is < 0 or > 2147483648
            || !(Content.ContentInstallFiles.AllowedUri(file.Uri) || Content.CurseForgePackResolver.AllowedCurseForgeUri(file.Uri)))
            throw new InvalidDataException("资源文件身份无效。");
        var path = SafePath.Within(Paths.CacheDirectory, "content/" + file.Sha1.ToLowerInvariant() + "/" + file.FileName);
        await ExecuteAsync("Installing", async ct =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            installCancellation = cancellation;
            var ownership = downloadOwners.Begin("content:" + file.Sha1);
            contentTransfers[ownership.Generation] = file;
            try
            {
                if (Content.CurseForgePackResolver.AllowedCurseForgeUri(file.Uri))
                {
                    using var transport = ContentTransport();
                    await new Content.CurseForgePackResolver(transport, null).DownloadAsync(new(0, 0, file.FileName, file.Sha1, null, file.Size, file.Uri, false), path, new InlineProgress<TransferProgress>(OnTransfer), cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    var urls = ModDownloadSources.Resolve(file.Uri.AbsoluteUri, Sources()).Select(s => new Uri(s)).ToArray();
                    await downloader.DownloadAsync(new(urls, path, file.Sha1, Size: file.Size), new InlineProgress<TransferProgress>(OnTransfer), cancellation.Token).ConfigureAwait(false);
                }
            }
            finally { downloadOwners.End(ownership); installCancellation = null; }
        }, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
