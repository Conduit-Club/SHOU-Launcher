using PCL.Core.Conduit;

namespace SHOU.Launcher.Core.Abstractions;

public sealed record ClubUpdateHistory(IReadOnlyList<ClubUpdateNotice> Notices, DateTimeOffset FetchedAt,
    bool FromCache = false, string? Warning = null);
public interface IClubContentService
{
    Task<ClubUpdateHistory> GetClubUpdatesAsync(bool refresh = false, CancellationToken cancellationToken = default);
    Task<ClubUpdateArticle> GetClubUpdateArticleAsync(ClubUpdateNotice notice, bool refresh = false, CancellationToken cancellationToken = default);
}
