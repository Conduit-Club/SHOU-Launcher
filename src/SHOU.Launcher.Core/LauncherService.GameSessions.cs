using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private Task? sessionMonitor;
    private void EnsureInstanceNotRunning(GameInstance instance)
    {
        if (games.TryGetValue(instance.Id, out var owned) && !owned.Process.Completion.IsCompleted)
            throw new InvalidOperationException("此实例的游戏仍在运行。请先退出此游戏，或选择其它实例。");
        gameSessions.EnsureWritable(instance);
    }
    private void RefreshGameSessions()
    {
        var active = new List<GameSessionSnapshot>();
        foreach (var instance in Snapshot.Instances)
        {
            GameSessionSnapshot? registered = null;
            try { registered = gameSessions.Read(instance); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Mutation still fails closed through EnsureWritable. */ }
            if (registered is not null) active.Add(registered);
            else if (games.TryGetValue(instance.Id, out var owned) && !owned.Process.Completion.IsCompleted)
                active.Add(new(instance.Id, owned.Process.ProcessId,
                    Snapshot.GameSessions.FirstOrDefault(s => s.InstanceId == instance.Id)?.StartedAt ?? DateTimeOffset.UtcNow));
        }
        var next = active.OrderBy(s => s.InstanceId, StringComparer.Ordinal).ToArray();
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
