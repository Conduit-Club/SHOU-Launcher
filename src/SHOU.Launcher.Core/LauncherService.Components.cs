using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceComponentService
{
    public async Task<GameInstance> ChangeInstanceLoaderAsync(string instanceId, string loader, string? loaderVersion, CancellationToken cancellationToken = default)
    {
        if (loader is not ("vanilla" or "fabric" or "quilt" or "forge" or "neoforge")) throw new NotSupportedException("此加载器暂不支持实例内修改。");
        if (loader != "vanilla") SafePath.Identifier(loaderVersion ?? throw new ArgumentException("请明确选择加载器版本。"));
        GameInstance? updated = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var instance = FindInstance(instanceId); EnsureInstanceNotRunning(instance); EnsureIsolatedGamePath(instance.GameDirectory);
            var game = InstanceIdentity.GameVersion(instance); var profile = game;
            if (loader == "fabric") profile = await new FabricMetadataProvider(http, Paths.CacheDirectory, Sources).CacheProfileAsync(game, loaderVersion!, ct).ConfigureAwait(false);
            else if (loader == "quilt") profile = await CacheQuiltProfileAsync(game, loaderVersion!, ct).ConfigureAwait(false);
            else if (loader is "forge" or "neoforge") profile = (await OfficialLoaders.CacheAsync(loader, game, loaderVersion!, ct).ConfigureAwait(false)).ProfileId;
            var candidate = instance with { VersionId = profile, GameVersionId = game, Loader = loader, LoaderVersion = loader == "vanilla" ? null : loaderVersion,
                RuntimeProfileOverride = instance.Origin is not null, Installed = false };
            // Preparation writes verified shared caches; the selected instance identity changes only after success.
            await PrepareInstanceAsync(candidate, ct, publishInstalled: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); EnsureInstanceNotRunning(instance);
            updated = candidate with { Installed = true };
            var next = Snapshot with { Instances = Snapshot.Instances.Select(i => i.Id == instance.Id ? updated : i).ToArray() };
            await PersistCandidateAsync(next, ct).ConfigureAwait(false); Set(s => s with { Instances = next.Instances });
        }, cancellationToken).ConfigureAwait(false);
        return updated!;
    }
}
