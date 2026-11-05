using System.Collections.Concurrent;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILauncherCatalogService
{
    private readonly ConcurrentDictionary<string, ContentFile> contentTransfers = new(StringComparer.Ordinal);
    private LauncherCatalog Catalog => new(http, Sources);
    public async Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.GamesAsync(cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.FabricAsync(gameVersion, cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.SearchAsync(query, cancellationToken).ConfigureAwait(false); }
    public async Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.ReleasesAsync(projectId, gameVersion, loader, cancellationToken).ConfigureAwait(false); }
    public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => Catalog.IconAsync(iconUrl, cancellationToken);
    public async Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default)
    {
        if (file.FileName.Length is 0 or > 200 || file.FileName.Contains('/') || file.FileName.Contains('\\') || file.FileName is "." or ".."
            || file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit) || file.Size is < 0 or > 2147483648
            || file.Uri.Scheme != "https" || !file.Uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(file.Uri.UserInfo))
            throw new InvalidDataException("资源文件身份无效。");
        var path = SafePath.Within(Paths.CacheDirectory, "content/" + file.Sha1.ToLowerInvariant() + "/" + file.FileName);
        await ExecuteAsync("Installing", async ct =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            installCancellation = cancellation;
            contentTransfers[file.Sha1] = file;
            var ownership = downloadOwners.Begin("content:" + file.Sha1);
            try
            {
                var urls = ModDownloadSources.Resolve(file.Uri.AbsoluteUri, Sources()).Select(s => new Uri(s)).ToArray();
                await downloader.DownloadAsync(new(urls, path, file.Sha1, Size: file.Size), new InlineProgress<TransferProgress>(OnTransfer), cancellation.Token).ConfigureAwait(false);
            }
            finally { downloadOwners.End(ownership); installCancellation = null; }
        }, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
