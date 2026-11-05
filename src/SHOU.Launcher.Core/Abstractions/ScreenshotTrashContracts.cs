namespace SHOU.Launcher.Core.Abstractions;

public sealed record ScreenshotTrashEntry(string Id, string RelativePath, long Bytes, string Sha256, DateTimeOffset MovedAt);
public interface IScreenshotTrashService
{
    Task<IReadOnlyList<ScreenshotTrashEntry>> ListScreenshotTrashAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<ScreenshotTrashEntry> MoveScreenshotToTrashAsync(string instanceId, string relativePath, CancellationToken cancellationToken = default);
    Task RestoreScreenshotAsync(string instanceId, string trashId, CancellationToken cancellationToken = default);
}
