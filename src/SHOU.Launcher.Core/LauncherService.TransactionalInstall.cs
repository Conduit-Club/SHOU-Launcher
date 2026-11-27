using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ITransactionalInstanceInstaller
{
    public async Task<GameInstance> CreateInstalledInstanceAsync(string name, string gameVersion, string loader, string? loaderVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("请输入 1–80 字的实例名称。");
        SafePath.Identifier(gameVersion);
        if (loader == "cleanroom") { if (gameVersion != "1.12.2") throw new InvalidDataException("Cleanroom 仅支持 1.12.2。"); SafePath.Identifier(loaderVersion ?? throw new InvalidDataException("Cleanroom 版本缺失。")); }
        else if (loader == "legacyfabric") SafePath.Identifier(loaderVersion ?? throw new InvalidDataException("Legacy Fabric 版本缺失。"));
        else if (Loaders.PortableVendorInstaller.Supports(loader)) SafePath.Identifier(loaderVersion ?? throw new InvalidDataException("加载器版本缺失。"));
        else PackLoaderIdentity.Validate(loader, loaderVersion);
        GameInstance? result = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var root = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory;
            EnsureIsolatedGamePath(root); VerifiedDownloader.RejectLink(root); Directory.CreateDirectory(root);
            var stage = ContentInstallFiles.Literal(root, ".shou-install-" + Guid.NewGuid().ToString("N"));
            var destination = NewGameDirectory(loader); var isShared = Path.GetFileName(destination) == "shared-game";
            SharedInstallPublication? shared = null;
            var moved = false; var registered = false;
            try
            {
                var profile = await CacheLoaderProfileAsync(loader, gameVersion, loaderVersion, ct).ConfigureAwait(false);
                var instance = new GameInstance(Guid.NewGuid().ToString("N"), name.Trim(), profile, stage, Loader: loader, LoaderVersion: loaderVersion, GameVersionId: gameVersion);
                Directory.CreateDirectory(stage);
                await PrepareInstanceAsync(instance, ct, publishInstalled: false).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (isShared)
                {
                    var existing = Snapshot.Instances.Where(i => PathEquals(i.GameDirectory, destination)).ToArray();
                    foreach (var other in existing) EnsureInstanceNotRunning(other);
                    Downloads.VerifiedDownloader.RejectLink(destination);
                    if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && existing.Length == 0) throw new IOException("共享目录含尚未接入的数据，请先接入已有实例。");
                    shared = new(destination); await shared.PublishAsync(stage, ct).ConfigureAwait(false);
                }
                else { await DirectoryPublication.PublishAsync(stage, destination, ct).ConfigureAwait(false); moved = true; }
                instance = instance with { GameDirectory = destination, Installed = true };
                var candidate = Snapshot with { Instances = Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = Snapshot.Settings.Downloads?.SelectInstalledInstance == false ? Snapshot.SelectedInstanceId : instance.Id };
                await PersistCandidateAsync(candidate, ct).ConfigureAwait(false); registered = true;
                shared?.Complete();
                Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = candidate.SelectedInstanceId }); result = instance;
            }
            finally
            {
                try { if (shared is not null) await shared.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    var owned = moved ? registered ? null : destination : stage;
                    if (owned is not null) { VerifiedDownloader.RejectLink(owned); if (Directory.Exists(owned)) Directory.Delete(owned, true); }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
