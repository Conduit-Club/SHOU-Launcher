using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Tools;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IToolboxService
{
    public async Task DownloadToolFileAsync(Uri source, string destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false)) { Timeout = Timeout.InfiniteTimeSpan };
        await ToolFileTransfer.DownloadAsync(client, source, destination, progress, cancellationToken).ConfigureAwait(false);
    }
    public async Task<AccountTextureImage> DownloadPublicSkinAsync(string playerName, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        return await new AccountAppearanceClient(client).ReadPublicMicrosoftSkinAsync(playerName, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ClubServer> QueryToolServerAsync(string address, CancellationToken cancellationToken = default)
    {
        address = address.Trim();
        if (address.Length is 0 or > 253 || address.Any(char.IsWhiteSpace) || address.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0
            || !Uri.TryCreate("minecraft://" + address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Port is 0 or > 65535)
            throw new ArgumentException("请输入服务器地址，可带端口；IPv6 地址请加方括号。");
        return await ClubStatusProbe.ProbeAsync(new("tool-query", "服务器", address), cancellationToken).ConfigureAwait(false);
    }
    public async Task<GameLogCleanupPlan> ScanGameLogsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); RequireNoToolGame();
        foreach (var instance in Snapshot.Instances) EnsureIsolatedGamePath(instance.GameDirectory);
        return await GameLogCleaner.ScanAsync(Snapshot.Instances, cancellationToken).ConfigureAwait(false);
    }
    public Task MoveGameLogsAsync(GameLogCleanupPlan plan, CancellationToken cancellationToken = default)
        => ExecuteAsync("Ready", async ct => { RequireNoToolGame(); foreach (var instance in Snapshot.Instances) EnsureIsolatedGamePath(instance.GameDirectory); await GameLogCleaner.MoveAsync(plan, Snapshot.Instances, Path.Combine(Paths.LocalDataDirectory, "tool-log-receipts"), ct); }, cancellationToken);
    public async Task<int> RestoreGameLogsAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        await ExecuteAsync("Ready", async ct => { RequireNoToolGame(); foreach (var instance in Snapshot.Instances) EnsureIsolatedGamePath(instance.GameDirectory); count = await GameLogCleaner.RestoreLatestAsync(Snapshot.Instances, Path.Combine(Paths.LocalDataDirectory, "tool-log-receipts"), ct); }, cancellationToken).ConfigureAwait(false);
        return count;
    }
    private void RequireNoToolGame()
    {
        RefreshGameSessions();
        if (Snapshot.GameSessions.Count != 0 || Snapshot.GameProcessId is not null) throw new InvalidOperationException("游戏仍在运行，请退出后再整理日志。");
    }
    public async Task<int> GetDailyFortuneAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var file = Path.Combine(Paths.LocalDataDirectory, "tool-fortune-seed.bin"); VerifiedDownloader.RejectLink(file);
        byte[] seed;
        if (!File.Exists(file))
        {
            seed = RandomNumberGenerator.GetBytes(32);
            var stage = file + "." + Guid.NewGuid().ToString("N") + ".part"; var owned = false;
            try
            {
                await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; await output.WriteAsync(seed, cancellationToken); await output.FlushAsync(cancellationToken); }
                cancellationToken.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(file); File.Move(stage, file, false); owned = false;
            }
            catch (IOException) when (File.Exists(file)) { seed = await ReadSeedAsync(file, cancellationToken); }
            finally { if (owned) File.Delete(stage); }
        }
        else seed = await ReadSeedAsync(file, cancellationToken);
        if (seed.Length != 32) throw new InvalidDataException("今日人品记录损坏，请保留文件后重试。");
        var data = System.Text.Encoding.UTF8.GetBytes(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(HMACSHA256.HashData(seed, data)) % 101);
    }
    private static async Task<byte[]> ReadSeedAsync(string file, CancellationToken ct)
    { VerifiedDownloader.RejectLink(file); if (new FileInfo(file).Length != 32) throw new InvalidDataException("今日人品记录损坏，请保留文件后重试。"); return await File.ReadAllBytesAsync(file, ct); }
}
