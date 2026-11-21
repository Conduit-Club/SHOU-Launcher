using System.Collections.Concurrent;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IWorldDatapackBackupService
{
    private sealed record DatapackRestorePlan(GameInstance Instance, string World, string BackupId, string WorkspaceRevision, string ReceiptRevision, DateTimeOffset Created);
    private readonly ConcurrentDictionary<string, DatapackRestorePlan> datapackRestores = new();
    public async Task<IReadOnlyList<WorldDatapackBackupEntry>> GetWorldDatapackBackupsAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken); return await WorldStore(instanceId, false).GetDatapackBackupsAsync(worldDirectoryName, cancellationToken); }
    public async Task<string> GetWorldDatapackBackupDirectoryAsync(string instanceId, string worldDirectoryName, string backupId, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); return WorldStore(instanceId, false).GetDatapackBackupDirectory(worldDirectoryName, backupId); }
    public async Task<WorldDatapackRestorePreview> PreviewWorldDatapackRestoreAsync(string instanceId, string worldDirectoryName, string backupId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); var instance = ContentTarget(instanceId, true); var state = await WorldStore(instanceId, true).PrepareDatapackRestoreAsync(worldDirectoryName, backupId, cancellationToken);
        var world = await WorldStore(instanceId, false).ReadDatapackWorkspaceAsync(worldDirectoryName, cancellationToken);
        if (world.Version != InstanceIdentity.GameVersion(instance)) throw new InvalidDataException("备份世界与目标实例版本不一致。");
        var id = Guid.NewGuid().ToString("N"); datapackRestores[id] = new(instance, worldDirectoryName, backupId, state.WorkspaceRevision, state.Revision, DateTimeOffset.UtcNow);
        foreach (var key in datapackRestores.Keys.Where(k => k != id).Take(Math.Max(0, datapackRestores.Count - 8))) datapackRestores.TryRemove(key, out _);
        return new(id, backupId, state.Journal.Files.Select(f => f.OldName is { } name ? f.NewName + " → " + name : "移回备份中保存：" + f.NewName).Append("恢复更新前的启停顺序和来源记录").ToArray(), ["仅在当前文件仍与更新完成时一致时恢复；替换下来的版本与原备份均保留。"]);
    }
    public async Task<WorldDatapackRestoreReceipt> RestoreWorldDatapackBackupAsync(string previewId, CancellationToken cancellationToken = default)
    {
        if (!datapackRestores.TryGetValue(previewId, out var plan) || DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(30)) throw new InvalidOperationException("恢复预检已失效，请重新检查。");
        WorldDatapackRestoreReceipt? result = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var instance = ContentTarget(plan.Instance.Id, true);
            if (instance.GameDirectory != plan.Instance.GameDirectory || InstanceIdentity.GameVersion(instance) != InstanceIdentity.GameVersion(plan.Instance)) throw new IOException("恢复目标实例已改变。");
            result = await WorldStore(instance.Id, true).RestoreDatapackBackupAsync(plan.World, plan.BackupId, plan.WorkspaceRevision, plan.ReceiptRevision, ct);
            datapackRestores.TryRemove(previewId, out _);
        }, cancellationToken);
        return result!;
    }
    public Task DiscardWorldDatapackRestoreAsync(string previewId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); datapackRestores.TryRemove(previewId, out _); return Task.CompletedTask; }
}
