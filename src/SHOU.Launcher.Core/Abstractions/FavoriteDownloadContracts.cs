namespace SHOU.Launcher.Core.Abstractions;

public sealed record FavoriteDownloadEntry(string ProjectId, string Title, ContentFile File, string DestinationName);
public sealed record FavoriteDownloadFailure(string Name, string Reason);
public sealed record FavoriteDownloadResult(int Added, int Reused, IReadOnlyList<FavoriteDownloadFailure> Failures);
public interface IContentBatchDownloadService
{
    Task<FavoriteDownloadResult> DownloadFavoriteFilesAsync(IReadOnlyList<FavoriteDownloadEntry> files, string directory, CancellationToken cancellationToken = default);
}
