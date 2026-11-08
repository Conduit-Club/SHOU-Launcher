using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IExistingInstanceService, IExistingInstanceBatchService
{
    public async Task<ExistingInstanceInspection> InspectInstanceFolderDetailedAsync(string folder, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await ExistingInstanceReader.InspectDetailedAsync(folder, cancellationToken); }
    public async Task<IReadOnlyList<ExistingInstanceCandidate>> InspectInstanceFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ExistingInstanceReader.InspectAsync(folder, cancellationToken).ConfigureAwait(false);
    }
    public async Task<GameInstance> AttachExistingInstanceAsync(ExistingInstanceCandidate candidate, CancellationToken cancellationToken = default)
    {
        var rows = await AttachExistingInstancesAsync([candidate], cancellationToken); return rows.Single();
    }
    public async Task<IReadOnlyList<GameInstance>> AttachExistingInstancesAsync(IReadOnlyList<ExistingInstanceCandidate> candidates, CancellationToken cancellationToken = default)
    {
        var owned = candidates.ToArray(); if (owned.Length is 0 or > 1000) throw new ArgumentException("请选择 1–1000 个预检通过的实例。");
        var attached = new List<GameInstance>();
        await ExecuteAsync("Ready", async ct =>
        {
            foreach (var candidate in owned)
            {
            ct.ThrowIfCancellationRequested();
            var refreshed = (await ExistingInstanceReader.InspectAsync(candidate.Origin.VersionDirectory, ct)).SingleOrDefault(c => c == candidate)
                ?? throw new IOException("实例目录已变化，请重新选择。");
            Downloads.VerifiedDownloader.RejectLink(refreshed.GameDirectory);
            if (Snapshot.Instances.Concat(attached).Any(i => i.Origin is { } origin && PathEquals(origin.VersionDirectory, refreshed.Origin.VersionDirectory) || PathEquals(i.GameDirectory, refreshed.GameDirectory) && i.VersionId == refreshed.VersionId)) throw new InvalidOperationException("此实例已经接入，请取消重复项后重试。");
            attached.Add(new(Guid.NewGuid().ToString("N"), refreshed.Name, refreshed.VersionId, refreshed.GameDirectory,
                Loader: refreshed.Loader, LoaderVersion: refreshed.LoaderVersion, Installed: true, GameVersionId: refreshed.GameVersion, Origin: refreshed.Origin, Presentation: refreshed.Presentation));
            }
            ct.ThrowIfCancellationRequested();
            foreach (var instance in attached) _ = await ExistingInstanceReader.ResolveAsync(instance.Origin!, instance.VersionId, instance.GameVersionId!, ct);
            var next = Snapshot with { Instances = Snapshot.Instances.Concat(attached).ToArray(), SelectedInstanceId = attached[0].Id };
            await PersistCandidateAsync(next, ct); Set(_ => next);
        }, cancellationToken).ConfigureAwait(false);
        return attached;
    }
    private static bool IsExplicitInstanceDirectory(GameInstance instance, string path)
    {
        if (instance.Origin is not { } origin || !PathEquals(instance.GameDirectory, path)) return false;
        if (origin.OriginalVersionId.Length is 0 or > 180 || origin.OriginalVersionId is "." or ".." || origin.OriginalVersionId.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        // Only the explicitly selected version or its owning Minecraft root can be authorized, never arbitrary siblings.
        return PathEquals(path, origin.MinecraftRoot) || PathEquals(path, origin.VersionDirectory);
    }
}
