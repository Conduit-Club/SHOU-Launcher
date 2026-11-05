using System.Collections.Concurrent;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IContentInstallationService, IContentDependencyCatalogService
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> contentOperationRetries = new(StringComparer.Ordinal);
    public async Task<ContentRelease> GetContentReleaseAsync(string releaseId, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.ReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false); }
    public async Task<ContentProject> GetContentProjectAsync(string projectId, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await Catalog.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false); }
    private GameInstance ContentTarget(string instanceId, bool mutate)
    {
        var instance = FindInstance(instanceId); EnsureIsolatedGamePath(instance.GameDirectory);
        _ = ContentInstallFiles.Literal(instance.GameDirectory, ContentInstallFiles.ManifestName);
        if (!Directory.Exists(instance.GameDirectory)) throw new DirectoryNotFoundException("实例游戏目录不存在，请重新选择实例。");
        if (mutate && (game is not null || Snapshot.GameProcessId is not null)) throw new InvalidOperationException("请先停止游戏再装入资源或导出整合包。");
        return instance;
    }
    public async Task<ContentInstallPlan> PlanContentInstallAsync(ContentInstallRequest request, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var instance = ContentTarget(request.InstanceId, false);
        return await new ContentInstallPlanner(this, this).PlanAsync(instance, InstanceIdentity.GameVersion(instance), request, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ContentInstallResult> InstallContentAsync(ContentInstallRequest request, CancellationToken cancellationToken = default)
    {
        ContentInstallResult? result = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var instance = ContentTarget(request.InstanceId, true);
            var plan = await new ContentInstallPlanner(this, this).PlanAsync(instance, InstanceIdentity.GameVersion(instance), request, ct).ConfigureAwait(false);
            await RunContentTransferAsync("resource-install:" + instance.Id, token => InstallContentAsync(request, token), async token =>
            {
                result = await ContentInstallFiles.InstallAsync(instance, plan, DownloadInstallFileAsync, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    private Task DownloadInstallFileAsync(ContentFile file, string target, CancellationToken ct)
    {
        ContentInstallFiles.ValidateFile(file);
        var sources = ModDownloadSources.Resolve(file.Uri.AbsoluteUri, Sources()).Select(s => new Uri(s)).ToArray();
        return downloader.DownloadAsync(new(sources, target, file.Sha1, Size: file.Size), new InlineProgress<TransferProgress>(OnTransfer), ct);
    }
    private async Task RunContentTransferAsync(string identity, Func<CancellationToken, Task> retry,
        Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ownership = downloadOwners.Begin(identity); installCancellation = cancellation;
        contentOperationRetries[ownership.Generation] = retry;
        if (contentOperationRetries.Count > 250)
            foreach (var key in contentOperationRetries.Keys.Where(k => k != ownership.Generation).Take(contentOperationRetries.Count - 250)) contentOperationRetries.TryRemove(key, out _);
        try { await action(cancellation.Token).ConfigureAwait(false); }
        finally { downloadOwners.End(ownership); installCancellation = null; }
    }
    private async Task<bool> TryRetryContentOperationAsync(string generation, CancellationToken ct)
    {
        if (!contentOperationRetries.TryGetValue(generation, out var retry)) return false;
        await retry(ct).ConfigureAwait(false); return true;
    }
}
