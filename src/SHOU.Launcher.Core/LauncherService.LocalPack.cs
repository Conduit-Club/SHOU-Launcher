using System.Collections.Concurrent;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILocalPackProgressService, ILocalPackManualFileService
{
    private readonly CurseForgePackResolver? localPackResolver;
    private sealed record LocalPackPlan(IReadOnlyList<CurseForgePackFile> Files, IReadOnlyList<string> Warnings);
    private readonly ConcurrentDictionary<string, LocalPackPlan> localPackPlans = new(StringComparer.OrdinalIgnoreCase);
    public Task<LocalPackInspection> InspectLocalPackAsync(string archivePath, CancellationToken cancellationToken = default)
        => InspectLocalPackCoreAsync(archivePath, null, cancellationToken);
    public Task<LocalPackInspection> InspectLocalPackWithProgressAsync(string archivePath, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        => InspectLocalPackCoreAsync(archivePath, new PackProgressReporter(progress), cancellationToken);
    private async Task<LocalPackInspection> InspectLocalPackCoreAsync(string archivePath, IProgress<PackInstallProgress>? progress, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var read = await new LocalPackArchive().ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (read.Inspection.Format != LocalPackFormat.CurseForge || !read.CanResolveCurseForge) return read.Inspection;
        using var dedicated = localPackResolver is null ? new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false)) : null;
        var resolver = localPackResolver ?? new CurseForgePackResolver(dedicated!, build.CurseForgeApiKey, Paths.CacheDirectory);
        var identity = read.Inspection;
        var resolution = await resolver.ResolveAsync(read.CurseForgeFiles, identity.GameVersion, identity.Loader, cancellationToken,
            new(identity.Name, identity.PackVersion ?? "", identity.GameVersion, identity.Loader, identity.LoaderVersion), progress).ConfigureAwait(false);
        var warnings = read.Inspection.Warnings.Concat(resolution.Warnings).Concat(resolution.BlockedFiles.Select(item => "未完成：" + item)).ToArray();
        if (resolution.BlockedFiles.Count > 0) return read.Inspection with { Warnings = warnings, ManualFiles = resolution.ManualFiles,
            CapabilityReason = $"有 {resolution.BlockedFiles.Count} 项必需文件尚待补充或核实，未创建实例。" + (resolution.ManualFiles?.Count > 0 ? "请从官网获取指定文件，校验后继续安装。" : "请查看完整阻塞列表后重新预检。") };
        ValidateLocalPackTargets(read, resolution.Files);
        cancellationToken.ThrowIfCancellationRequested(); localPackPlans[read.Inspection.ArchiveSha256] = new(resolution.Files.ToArray(), warnings);
        while (localPackPlans.Count > 8) { var other = localPackPlans.Keys.FirstOrDefault(k => k != read.Inspection.ArchiveSha256); if (other is null) break; localPackPlans.TryRemove(other, out _); }
        return read.Inspection with { CanImport = true, CapabilityReason = null, Warnings = warnings, LocalFiles = resolution.Files.Select(f => f.Path).Concat(read.OverrideEntries.Keys).Distinct(StringComparer.Ordinal).ToArray() };
    }
    public Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request, CancellationToken cancellationToken = default)
        => ImportLocalPackCoreAsync(request, null, cancellationToken);
    public Task<LocalPackImportResult> ImportLocalPackWithProgressAsync(LocalPackImportRequest request, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        => ImportLocalPackCoreAsync(request, new PackProgressReporter(progress), cancellationToken);
    private async Task<LocalPackImportResult> ImportLocalPackCoreAsync(LocalPackImportRequest request, IProgress<PackInstallProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); request = request with { OptionalClientFiles = request.OptionalClientFiles?.ToArray() };
        if (string.IsNullOrWhiteSpace(request.InstanceName) || request.InstanceName.Length > 80 || request.InstanceName.Any(char.IsControl)) throw new ArgumentException("请输入 1–80 字的单行实例名称。");
        if (!ContentInstallFiles.Hash(request.ExpectedSha256, 64)) throw new InvalidDataException("请先完成整合包预检。");
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var read = await new LocalPackArchive().ReadAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
        if (!read.Inspection.ArchiveSha256.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("整合包自预检后已改变，请重新预检。");
        if (read.Inspection.Format == LocalPackFormat.Modrinth)
        {
            var result = await ImportModrinthPackCoreAsync(new(request.ArchivePath, request.ExpectedSha256, request.InstanceName, request.OptionalClientFiles, request.PrepareAfterImport), progress, cancellationToken).ConfigureAwait(false);
            return new(result.Instance, result.InstalledFiles, read.Inspection.Warnings, result);
        }
        var isShou = read.Inspection.Format == LocalPackFormat.ShouExport && read.Inspection.CanImport;
        if (!isShou && (read.Inspection.Format != LocalPackFormat.CurseForge || !read.CanResolveCurseForge)) throw new NotSupportedException(read.Inspection.CapabilityReason ?? "此格式尚未支持导入。");
        if (request.OptionalClientFiles is { Count: > 0 }) throw new NotSupportedException("本轮 CurseForge ZIP 仅导入 required 文件；可选文件不接受额外路径选择。");
        LocalPackPlan? plan = isShou ? new([], read.Inspection.Warnings) : null;
        if (!isShou && !localPackPlans.TryGetValue(request.ExpectedSha256, out plan))
        {
            var inspection = await InspectLocalPackCoreAsync(request.ArchivePath, progress, cancellationToken).ConfigureAwait(false);
            if (!inspection.CanImport || !localPackPlans.TryGetValue(request.ExpectedSha256, out plan)) throw new InvalidOperationException(inspection.CapabilityReason ?? "整合包文件尚未全部核实。");
        }
        var capturedPlan = plan!; LocalPackImportResult? imported = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var root = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory; EnsureIsolatedGamePath(root); VerifiedDownloader.RejectLink(root); Directory.CreateDirectory(root);
            var stage = ContentInstallFiles.Literal(root, ".shou-local-stage-" + Guid.NewGuid().ToString("N")); var destination = ContentInstallFiles.Literal(root, Guid.NewGuid().ToString("N")); var moved = false; var registered = false;
            using var dedicated = localPackResolver is null ? new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false)) : null;
            var resolver = localPackResolver ?? new CurseForgePackResolver(dedicated!, build.CurseForgeApiKey, Paths.CacheDirectory);
            try
            {
                await RunContentTransferAsync("local-pack:" + Guid.NewGuid().ToString("N"), token => ImportLocalPackAsync(request, token), async token =>
                {
                    var inspection = isShou ? await ShouPackArchive.StageAsync(request.ArchivePath, request.ExpectedSha256, stage, token).ConfigureAwait(false)
                        : await new LocalPackArchive().StageCurseForgeAsync(request.ArchivePath, request.ExpectedSha256, stage, capturedPlan.Files,
                        (file, path, transferToken) => resolver.DownloadAsync(file, path, new InlineProgress<TransferProgress>(OnTransfer), transferToken), token, progress).ConfigureAwait(false);
                    var profile = await CacheLoaderProfileAsync(inspection.Loader, inspection.GameVersion, inspection.LoaderVersion, token).ConfigureAwait(false);
                    var instance = new GameInstance(Guid.NewGuid().ToString("N"), request.InstanceName.Trim(), profile, destination, MemoryMiB: inspection.RecommendedMemoryMiB, Loader: inspection.Loader, LoaderVersion: inspection.LoaderVersion, GameVersionId: inspection.GameVersion);
                    var manifestPath = ContentInstallFiles.Literal(stage, ContentInstallFiles.ManifestName); var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false)) ?? throw new InvalidDataException("整合包安装清单缺失。");
                    await File.WriteAllBytesAsync(manifestPath, ContentInstallFiles.SerializeManifest(manifest with { InstanceId = instance.Id }), token).ConfigureAwait(false);
                    if (request.PrepareAfterImport)
                    {
                        await PrepareInstanceAsync(instance with { GameDirectory = stage }, token, publishInstalled: false).ConfigureAwait(false);
                        instance = instance with { Installed = true };
                    }
                    token.ThrowIfCancellationRequested(); _ = ContentInstallFiles.Literal(root, Path.GetFileName(stage)); _ = ContentInstallFiles.Literal(root, Path.GetFileName(destination)); await IO.DirectoryPublication.PublishAsync(stage, destination, token).ConfigureAwait(false); moved = true;
                    var candidate = Snapshot with { Instances = Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = Snapshot.Settings.Downloads?.SelectInstalledInstance == false ? Snapshot.SelectedInstanceId : instance.Id }; await PersistCandidateAsync(candidate, token).ConfigureAwait(false); registered = true;
                    Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = candidate.SelectedInstanceId }); imported = new(instance, manifest.Files.Count, capturedPlan.Warnings);
                }, ct).ConfigureAwait(false);
            }
            finally { if (!registered) { var owned = moved ? destination : stage; VerifiedDownloader.RejectLink(owned); if (Directory.Exists(owned)) Directory.Delete(owned, true); } }
        }, cancellationToken).ConfigureAwait(false);
        return imported!;
    }
    private static void ValidateLocalPackTargets(LocalPackDescriptor archive, IReadOnlyList<CurseForgePackFile> files)
    {
        var paths = files.Select(f => f.Path).Concat(archive.OverrideEntries.Keys).ToArray();
        if (archive.OverrideEntries.Keys.Any(path => files.Any(f => ContentInstallFiles.PathKey(f.Path) == ContentInstallFiles.PathKey(path) && f.Path != path))) throw new InvalidDataException("下载文件与覆盖文件存在大小写或 Unicode 歧义。");
        PackArchiveReader.CheckTargets(paths); var keys = paths.Select(ContentInstallFiles.PathKey).ToHashSet(StringComparer.Ordinal);
        if (files.Any(f => new[] { ".download.lock", ".part", ".part.resume.json" }.Any(suffix => keys.Contains(ContentInstallFiles.PathKey(f.Path + suffix))))) throw new InvalidDataException("整合包文件与下载暂存侧文件冲突。");
    }
}
