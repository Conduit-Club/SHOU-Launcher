using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceContentBrowserService
{
    public async Task<IReadOnlyList<InstanceContentEntry>> BrowseInstanceContentAsync(string instanceId, string kind, string directory, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); var instance = ContentTarget(instanceId, false);
        return await Task.Run(() => InstanceWorkspace.List(instance, kind, cancellationToken, directory, true), cancellationToken);
    }
    public async Task<string> GetInstanceContentDirectoryAsync(string instanceId, string kind, string directory, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
        var folder = InstanceWorkspace.ContentDirectory(ContentTarget(instanceId, false), kind, directory);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("资源目录已移动，请返回上一级。");
        return folder;
    }
    public Task ImportInstanceContentToDirectoryAsync(string instanceId, string kind, string directory, string sourcePath, CancellationToken cancellationToken = default)
        => ExecuteAsync("Ready", async ct =>
        {
            var instance = ContentTarget(instanceId, true);
            _ = InstanceWorkspace.ContentDirectory(instance, kind, directory);
            await InstanceWorkspace.ImportAsync(instance, kind, sourcePath, ct, directory);
        }, cancellationToken);
}
