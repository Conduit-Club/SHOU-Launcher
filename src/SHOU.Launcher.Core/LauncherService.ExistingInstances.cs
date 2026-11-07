using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IExistingInstanceService
{
    public async Task<IReadOnlyList<ExistingInstanceCandidate>> InspectInstanceFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ExistingInstanceReader.InspectAsync(folder, cancellationToken).ConfigureAwait(false);
    }
    public async Task<GameInstance> AttachExistingInstanceAsync(ExistingInstanceCandidate candidate, CancellationToken cancellationToken = default)
    {
        GameInstance? attached = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var refreshed = (await ExistingInstanceReader.InspectAsync(candidate.Origin.VersionDirectory, ct)).SingleOrDefault(c => c == candidate)
                ?? throw new IOException("实例目录已变化，请重新选择。");
            Downloads.VerifiedDownloader.RejectLink(refreshed.GameDirectory);
            if (Snapshot.Instances.Any(i => PathEquals(i.GameDirectory, refreshed.GameDirectory) && i.VersionId == refreshed.VersionId)) throw new InvalidOperationException("此实例已经接入。");
            attached = new(Guid.NewGuid().ToString("N"), refreshed.Name, refreshed.VersionId, refreshed.GameDirectory,
                Loader: refreshed.Loader, LoaderVersion: refreshed.LoaderVersion, Installed: true, GameVersionId: refreshed.GameVersion, Origin: refreshed.Origin);
            var next = Snapshot with { Instances = Snapshot.Instances.Append(attached).ToArray(), SelectedInstanceId = attached.Id };
            await PersistCandidateAsync(next, ct); Set(_ => next);
        }, cancellationToken).ConfigureAwait(false);
        return attached!;
    }
    private static bool IsExplicitInstanceDirectory(GameInstance instance, string path)
    {
        if (instance.Origin is not { } origin || !PathEquals(instance.GameDirectory, path)) return false;
        if (origin.OriginalVersionId.Length is 0 or > 180 || origin.OriginalVersionId is "." or ".." || origin.OriginalVersionId.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        // Only the explicitly selected version or its owning Minecraft root can be authorized, never arbitrary siblings.
        return PathEquals(path, origin.MinecraftRoot) || PathEquals(path, origin.VersionDirectory);
    }
}
