using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceWorldService
{
    private InstanceWorldStore WorldStore(string instanceId, bool mutate)
    {
        if (!string.Equals(Snapshot.SelectedInstanceId, instanceId, StringComparison.Ordinal)) throw new InvalidOperationException("Select this isolated instance before accessing its world files.");
        var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        if (mutate) EnsureInstanceNotRunning(instance);
        var boundary = Paths.DataDirectory;
        if (IsExplicitInstanceDirectory(instance, instance.GameDirectory))
            boundary = Path.GetDirectoryName(Path.GetFullPath(instance.GameDirectory)) ?? throw new IOException("实例目录不能是磁盘根目录。");
        else if (!IO.SafePath.IsWithin(IO.SafePath.Canonical(boundary), IO.SafePath.Canonical(instance.GameDirectory)))
        {
            var managedRoot = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory;
            if (PathEquals(managedRoot, instance.GameDirectory) || !IO.SafePath.IsWithin(IO.SafePath.Canonical(managedRoot), IO.SafePath.Canonical(instance.GameDirectory)))
                throw new IOException("请先接入此实例目录，再访问其存档或服务器文件。");
            boundary = managedRoot;
        }
        // The store still confines every operation to gameDirectory and captures physical node identities.
        return new(boundary, instance.GameDirectory);
    }
    public async Task<InstanceServerList> GetInstanceServersAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await WorldStore(instanceId, false).GetServersAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<InstanceServerList> SaveInstanceServersAsync(string instanceId, string expectedRevision, IReadOnlyList<InstanceServerEdit> entries, CancellationToken cancellationToken = default)
    {
        InstanceServerList? result = null;
        await ExecuteAsync("Ready", async ct => result = await WorldStore(instanceId, true).SaveServersAsync(expectedRevision, entries, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<IReadOnlyList<InstanceWorldSummary>> GetInstanceWorldsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await WorldStore(instanceId, false).GetWorldsAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<WorldDatapackFile>> GetWorldDatapacksAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await WorldStore(instanceId, false).GetDatapacksAsync(worldDirectoryName, cancellationToken).ConfigureAwait(false);
    }
    public async Task<WorldDatapackFile> ImportWorldDatapackAsync(string instanceId, string worldDirectoryName, string zipPath, CancellationToken cancellationToken = default)
    {
        WorldDatapackFile? result = null;
        await ExecuteAsync("Ready", async ct => result = await WorldStore(instanceId, true).ImportDatapackAsync(worldDirectoryName, zipPath, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
