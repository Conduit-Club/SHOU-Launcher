using System.Text.Json;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IModrinthPackService
{
    public async Task<ModrinthPackInspection> InspectModrinthPackAsync(string archivePath, CancellationToken cancellationToken = default)
    { await InitializeAsync(cancellationToken).ConfigureAwait(false); return await new ModrinthPackArchive().InspectAsync(archivePath, cancellationToken).ConfigureAwait(false); }
    public async Task<ModrinthPackImportResult> ImportModrinthPackAsync(ModrinthPackImportRequest request, CancellationToken cancellationToken = default)
    {
        request = request with { OptionalClientFiles = request.OptionalClientFiles?.ToArray() };
        if (string.IsNullOrWhiteSpace(request.InstanceName) || request.InstanceName.Length > 80 || request.InstanceName.Any(char.IsControl)) throw new ArgumentException("请输入1–80字的单行实例名称。");
        ModrinthPackImportResult? result = null;
        await ExecuteAsync("Installing", async ct =>
        {
            if (game is not null || Snapshot.GameProcessId is not null) throw new InvalidOperationException("请先停止游戏再导入整合包。");
            var root = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory; EnsureIsolatedGamePath(root); VerifiedDownloader.RejectLink(root); Directory.CreateDirectory(root);
            var stage = ContentInstallFiles.Literal(root, ".shou-pack-stage-" + Guid.NewGuid().ToString("N"));
            var destination = ContentInstallFiles.Literal(root, Guid.NewGuid().ToString("N")); var registered = false; var moved = false;
            try
            {
                await RunContentTransferAsync("mrpack-import:" + Guid.NewGuid().ToString("N"), token => ImportModrinthPackAsync(request, token), async token =>
                {
                    var inspection = await new ModrinthPackArchive().StageAsync(request.ArchivePath, request.ExpectedSha256, stage, request.OptionalClientFiles, DownloadPackFileAsync, token).ConfigureAwait(false);
                    var profile = inspection.Loader == "fabric"
                        ? await new FabricMetadataProvider(http, Paths.CacheDirectory, Sources).CacheProfileAsync(inspection.GameVersion, inspection.LoaderVersion!, token).ConfigureAwait(false)
                        : inspection.GameVersion;
                    var instance = new GameInstance(Guid.NewGuid().ToString("N"), request.InstanceName.Trim(), profile, destination,
                        Loader: inspection.Loader, LoaderVersion: inspection.LoaderVersion, GameVersionId: inspection.GameVersion);
                    var manifestPath = ContentInstallFiles.Literal(stage, ContentInstallFiles.ManifestName);
                    var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false)) ?? throw new InvalidDataException("整合包安装清单缺失。");
                    await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { InstanceId = instance.Id }), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); _ = ContentInstallFiles.Literal(root, Path.GetFileName(stage)); _ = ContentInstallFiles.Literal(root, Path.GetFileName(destination));
                    Directory.Move(stage, destination); moved = true;
                    var candidate = Snapshot with { Instances = Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = instance.Id };
                    await PersistCandidateAsync(candidate, token).ConfigureAwait(false); registered = true;
                    Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = instance.Id });
                    result = new(instance, manifest.Files.Count, inspection.Files.Count(f => f.Client == "unsupported"), inspection.Files.Count(f => f.Client == "optional" && !(request.OptionalClientFiles ?? []).Contains(f.Path)));
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                if (!registered)
                {
                    var owned = moved ? destination : stage; VerifiedDownloader.RejectLink(owned);
                    if (Directory.Exists(owned)) Directory.Delete(owned, true);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    private Task DownloadPackFileAsync(ModrinthPackFile file, string path, CancellationToken ct)
    {
        var urls = file.Downloads.SelectMany(url => ModDownloadSources.Resolve(url, Sources())).Distinct().Select(url => new Uri(url)).ToArray();
        return downloader.DownloadAsync(new(urls, path, file.Sha1, Size: file.Size), new InlineProgress<TransferProgress>(OnTransfer), ct);
    }
    public async Task<string> ExportModrinthPackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default)
    {
        options = options with { OverridePaths = options.OverridePaths?.ToArray() };
        string? result = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var instance = ContentTarget(instanceId, true);
            result = await new ModrinthPackArchive().ExportAsync(instance, options, SafePath.Within(Paths.CacheDirectory, "exports"), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
