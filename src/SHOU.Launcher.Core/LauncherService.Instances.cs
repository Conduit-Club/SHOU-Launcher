using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceWorkspaceService, IInstanceDirectoryService
{
    public async Task<string> GetInstanceDirectoryAsync(string instanceId, string? contentKind = null, bool createMissing = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
        var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        var folder = contentKind is null ? instance.GameDirectory : InstanceWorkspace.Folder(instance.GameDirectory, contentKind);
        Downloads.VerifiedDownloader.RejectLink(folder);
        if (!Directory.Exists(folder) && createMissing)
        {
            await ExecuteAsync("Ready", ct =>
            {
                ct.ThrowIfCancellationRequested(); EnsureInstanceNotRunning(instance); Downloads.VerifiedDownloader.RejectLink(folder);
                Directory.CreateDirectory(folder); Downloads.VerifiedDownloader.RejectLink(folder); return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("实例文件夹不存在，请检查实例路径。");
        return folder;
    }
    private async Task RegisterNewInstanceAsync(GameInstance instance, CancellationToken ct)
    {
        EnsureIsolatedGamePath(instance.GameDirectory); Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
        var shared = Path.GetFileName(instance.GameDirectory) == "shared-game";
        var existed = Directory.Exists(instance.GameDirectory);
        if (File.Exists(instance.GameDirectory) || existed && !shared) throw new IOException("新实例目录已存在，请重新创建。");
        if (shared && existed && Directory.EnumerateFileSystemEntries(instance.GameDirectory).Any() && !Snapshot.Instances.Any(i => PathEquals(i.GameDirectory, instance.GameDirectory))) throw new IOException("共用游戏目录包含未接入的数据，请先接入已有实例或选择其它根目录。");
        Directory.CreateDirectory(instance.GameDirectory);
        try
        {
            Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
            var candidate = Snapshot with { Instances = Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = Snapshot.Settings.Downloads?.SelectInstalledInstance == false ? Snapshot.SelectedInstanceId : instance.Id };
            await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
            Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = candidate.SelectedInstanceId });
        }
        catch
        {
            try
            {
                Downloads.VerifiedDownloader.RejectLink(instance.GameDirectory);
                if (!existed && Directory.Exists(instance.GameDirectory) && !Directory.EnumerateFileSystemEntries(instance.GameDirectory).Any()) Directory.Delete(instance.GameDirectory);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { Log("Warning", "未完成实例的空目录清理失败：" + cleanup.GetType().Name); }
            throw;
        }
    }
    public Task UpdateInstanceAsync(string instanceId, InstanceEdit edit, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 160 || edit.Name.Any(char.IsControl)) throw new ArgumentException("实例名称须为1–160字的单行文字。");
        if (edit.JavaExecutable is { Length: > 0 } java && !Path.IsPathFullyQualified(java)) throw new ArgumentException("Java路径须为绝对路径。");
        if (edit.MemoryMiB is < 512 or > 32768)
            throw new ArgumentException("实例最大内存须为512–32768 MiB且不小于初始内存。");
        var instance = FindInstance(instanceId);
        EnsureInstanceNotRunning(instance);
        if (instance.Origin is { } origin && (edit.DirectoryMode is not null || edit.LaunchOptions != instance.LaunchOptions)) _ = await ExistingInstanceReader.ResolveAsync(origin, instance.VersionId, InstanceIdentity.GameVersion(instance), ct);
        if (edit.LaunchOptions is { } options) Minecraft.GameSettingsPolicy.Validate(options);
        if (edit.LaunchOptions is { InheritGlobal: false } localOptions) _ = Minecraft.GameSettingsPolicy.Classpath(localOptions.ClasspathPrepend);
        var effectiveMinimum = Minecraft.GameSettingsPolicy.Options(instance with { LaunchOptions = edit.LaunchOptions }, Snapshot.Settings).MinimumMemoryMiB ?? Snapshot.Settings.MinimumMemoryMiB;
        if (effectiveMinimum > (edit.MemoryMiB ?? Snapshot.Settings.MemoryMiB)) throw new ArgumentException("实例最大内存不能低于其有效初始内存。");
        var next = instance with { Name = edit.Name.Trim(), JavaExecutable = string.IsNullOrWhiteSpace(edit.JavaExecutable) ? null : edit.JavaExecutable, MemoryMiB = edit.MemoryMiB, LaunchOptions = edit.LaunchOptions };
        if (edit.DirectoryMode is { } directoryMode)
        {
            var directories = InstanceDirectoryPolicy.Describe(instance, Paths) with { Mode = directoryMode };
            var target = InstanceDirectoryPolicy.Resolve(instance, directories, Snapshot.Settings);
            if (!PathEquals(target, instance.GameDirectory) && !edit.ConfirmDirectoryChange) throw new InvalidOperationException("请确认切换游戏目录；原目录文件不会移动或合并。");
            next = RebindDirectory(next, directories, Snapshot.Settings);
        }
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
