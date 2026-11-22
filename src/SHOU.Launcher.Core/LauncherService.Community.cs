using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Community;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IClubContentService
{
    public async Task<ClubUpdateHistory> GetClubUpdatesAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        return await new ClubUpdatesClient(client, Path.Combine(Paths.CacheDirectory, "club-updates")).GetHistoryAsync(refresh, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ClubUpdateArticle> GetClubUpdateArticleAsync(ClubUpdateNotice notice, bool refresh = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        return await new ClubUpdatesClient(client, Path.Combine(Paths.CacheDirectory, "club-updates")).GetArticleAsync(notice, refresh, cancellationToken).ConfigureAwait(false);
    }
}
