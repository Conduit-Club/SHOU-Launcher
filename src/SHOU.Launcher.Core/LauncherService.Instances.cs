using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceWorkspaceService
{
    private async Task RegisterNewInstanceAsync(GameInstance instance, CancellationToken ct)
    {
        EnsureIsolatedGamePath(instance.GameDirectory); Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
        if (Directory.Exists(instance.GameDirectory) || File.Exists(instance.GameDirectory)) throw new IOException("新实例目录已存在，请重新创建。");
        Directory.CreateDirectory(instance.GameDirectory);
        try
        {
            Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
            var candidate = Snapshot with { Instances = Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = instance.Id };
            await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
            Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = instance.Id });
        }
        catch
        {
            try
            {
                Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
                if (Directory.Exists(instance.GameDirectory) && !Directory.EnumerateFileSystemEntries(instance.GameDirectory).Any()) Directory.Delete(instance.GameDirectory);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { Log("Warning", "未完成实例的空目录清理失败：" + cleanup.GetType().Name); }
            throw;
        }
    }
    public Task UpdateInstanceAsync(string instanceId, InstanceEdit edit, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 160 || edit.Name.Any(char.IsControl)) throw new ArgumentException("实例名称须为1–160字的单行文字。");
        if (edit.JavaExecutable is { Length: > 0 } java && !Path.IsPathFullyQualified(java)) throw new ArgumentException("Java路径须为绝对路径。");
        if (edit.MemoryMiB is < 512 or > 32768 || edit.MemoryMiB is { } memory && Snapshot.Settings.MinimumMemoryMiB > memory)
            throw new ArgumentException("实例最大内存须为512–32768 MiB且不小于初始内存。");
        var instance = FindInstance(instanceId);
        EnsureInstanceNotRunning(instance);
        var next = instance with { Name = edit.Name.Trim(), JavaExecutable = string.IsNullOrWhiteSpace(edit.JavaExecutable) ? null : edit.JavaExecutable, MemoryMiB = edit.MemoryMiB };
        var candidate = Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? next : i).ToArray() };
        await PersistCandidateAsync(candidate, ct).ConfigureAwait(false); Set(_ => candidate);
    }, cancellationToken);
    public async Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string instanceId, string kind, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        return InstanceWorkspace.List(instance, kind);
    }
    public Task ImportInstanceContentAsync(string instanceId, string kind, string sourcePath, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        EnsureInstanceNotRunning(instance);
        await InstanceWorkspace.ImportAsync(instance, kind, sourcePath, ct).ConfigureAwait(false);
    }, cancellationToken);
    public Task SetInstanceModEnabledAsync(string instanceId, string relativePath, bool enabled, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", ct =>
    {
        ct.ThrowIfCancellationRequested(); var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        EnsureInstanceNotRunning(instance);
        InstanceWorkspace.ToggleMod(instance, relativePath, enabled); return Task.CompletedTask;
    }, cancellationToken);
    public async Task<string> ExportInstanceAsync(string instanceId, bool includeWorlds, CancellationToken cancellationToken = default)
    {
        string? output = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
            EnsureInstanceNotRunning(instance);
            output = await InstanceWorkspace.ExportAsync(instance, SafePath.Within(Paths.CacheDirectory, "exports"), includeWorlds, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return output!;
    }
}
