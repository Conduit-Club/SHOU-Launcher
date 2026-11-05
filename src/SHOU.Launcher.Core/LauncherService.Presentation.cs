using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstancePresentationService
{
    public Task UpdateInstancePresentationAsync(string instanceId, InstancePresentationEdit edit, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        if (edit.Icon is not ("auto" or "minecraft" or "fabric" or "quilt" or "forge" or "neoforge")) throw new ArgumentException("未知实例图标。");
        if (string.IsNullOrWhiteSpace(edit.Category) || edit.Category.Length > 80 || edit.Category.Any(char.IsControl)) throw new ArgumentException("分类须为1–80字的单行文字。");
        if (edit.Description.Length > 4096 || edit.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new ArgumentException("实例描述最多4096字，不能包含控制字符。");
        var instance = FindInstance(instanceId); var previous = instance.Presentation ?? new();
        var updated = instance with { Presentation = previous with { Icon = edit.Icon, Category = edit.Category.Trim(), Description = edit.Description, Favorite = edit.Favorite } };
        var candidate = Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? updated : i).ToArray() };
        await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
        Set(s => s with { Instances = candidate.Instances });
    }, cancellationToken);

    private async Task RecordInstanceLaunchAsync(string instanceId)
    {
        try
        {
            var current = FindInstance(instanceId); var previous = current.Presentation ?? new();
            var updated = current with { Presentation = previous with { LaunchCount = previous.LaunchCount == long.MaxValue ? long.MaxValue : previous.LaunchCount + 1 } };
            var candidate = Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? updated : i).ToArray() };
            await PersistCandidateAsync(candidate, lifetime.Token).ConfigureAwait(false);
            Set(s => s with { Instances = candidate.Instances });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { Log("Warning", "游戏已启动，启动次数未能保存：" + ex.GetType().Name); }
    }
}
