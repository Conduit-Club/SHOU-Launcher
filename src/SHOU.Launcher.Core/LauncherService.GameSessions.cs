using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private Task? sessionMonitor;
    private bool IsGameDirectoryRunning(GameInstance instance) => Snapshot.Instances.Where(i => PathEquals(i.GameDirectory, instance.GameDirectory))
        .Any(i => games.Values.Any(g => g.InstanceId == i.Id && !g.Process.Completion.IsCompleted) || gameSessions.ReadAll(i).Count > 0);
    private void EnsureInstanceNotRunning(GameInstance instance)
    {
        foreach (var other in Snapshot.Instances.Where(i => i.Id != instance.Id && PathEquals(i.GameDirectory, instance.GameDirectory)))
        {
            if (games.Values.Any(g => g.InstanceId == other.Id && !g.Process.Completion.IsCompleted)) throw new InvalidOperationException("共用此游戏目录的另一实例仍在运行，请先退出。");
            gameSessions.EnsureWritable(other);
        }
        if (games.Values.Any(g => g.InstanceId == instance.Id && !g.Process.Completion.IsCompleted))
            throw new InvalidOperationException("此实例的游戏仍在运行。请先退出此游戏，或选择其它实例。");
        gameSessions.EnsureWritable(instance);
    }
    private void RefreshGameSessions()
    {
        var active = new List<GameSessionSnapshot>();
        foreach (var instance in Snapshot.Instances)
        {
            IReadOnlyList<GameSessionSnapshot> registered = [];
            try { registered = gameSessions.ReadAll(instance); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Mutation still fails closed through EnsureWritable. */ }
            active.AddRange(registered);
            foreach (var owned in games.Values.Where(g => g.InstanceId == instance.Id && !g.Process.Completion.IsCompleted && registered.All(s => s.ProcessId != g.Process.ProcessId)))
                active.Add(new(instance.Id, owned.Process.ProcessId,
                    Snapshot.GameSessions.FirstOrDefault(s => s.ProcessId == owned.Process.ProcessId)?.StartedAt ?? DateTimeOffset.UtcNow));
        }
        var next = active.DistinctBy(s => s.ProcessId).OrderBy(s => s.InstanceId, StringComparer.Ordinal).ThenBy(s => s.ProcessId).ToArray();
        if (Snapshot.GameSessions.SequenceEqual(next)) return;
        Set(s => s with { GameSessions = next, GameProcessId = next.LastOrDefault()?.ProcessId });
    }
    private async Task MonitorGameSessionsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) RefreshGameSessions(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private void SetLaunchStage(string stage) => Set(s => s.LaunchActivity is { } activity
        ? s with { LaunchActivity = activity with { Stage = stage, BytesReceived = 0, TotalBytes = null } } : s);
    private void SetLaunchTransfer(TransferProgress progress)
    {
        if (Snapshot.LaunchActivity is null) return;
        Set(s => s.LaunchActivity is { } activity ? s with { LaunchActivity = activity with {
            Stage = progress.Stage == "Completed" ? "校验下载文件" : "下载游戏文件",
            BytesReceived = progress.BytesReceived, TotalBytes = progress.TotalBytes } } : s);
    }
}
