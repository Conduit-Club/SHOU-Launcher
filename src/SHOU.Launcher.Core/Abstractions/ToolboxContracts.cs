namespace SHOU.Launcher.Core.Abstractions;

public interface IToolboxService
{
    Task DownloadToolFileAsync(Uri source, string destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<AccountTextureImage> DownloadPublicSkinAsync(string playerName, CancellationToken cancellationToken = default);
    Task<ClubServer> QueryToolServerAsync(string address, CancellationToken cancellationToken = default);
    Task<GameLogCleanupPlan> ScanGameLogsAsync(CancellationToken cancellationToken = default);
    Task MoveGameLogsAsync(GameLogCleanupPlan plan, CancellationToken cancellationToken = default);
    Task<int> RestoreGameLogsAsync(CancellationToken cancellationToken = default);
    Task<int> GetDailyFortuneAsync(DateOnly date, CancellationToken cancellationToken = default);
}

public sealed record GameLogFile(string InstanceId, string RelativePath, long Bytes, string Sha256);
public sealed record GameLogCleanupPlan(string Id, IReadOnlyList<GameLogFile> Files)
{
    public long Bytes => Files.Sum(f => f.Bytes);
}
