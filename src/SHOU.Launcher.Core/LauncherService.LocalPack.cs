using System.Collections.Concurrent;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILocalPackProgressService, ILocalPackManualFileService, ILocalPackSelectionService
{
    private readonly CurseForgePackResolver? localPackResolver;
    private sealed record LocalPackPlan(IReadOnlyList<CurseForgePackFile> Files, IReadOnlyList<string> Warnings);
    private readonly ConcurrentDictionary<string, LocalPackPlan> localPackPlans = new(StringComparer.OrdinalIgnoreCase);
    public Task<LocalPackInspection> InspectLocalPackAsync(string archivePath, CancellationToken cancellationToken = default)
        => InspectLocalPackCoreAsync(archivePath, null, cancellationToken);
    public Task<LocalPackInspection> InspectLocalPackWithProgressAsync(string archivePath, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        => InspectLocalPackCoreAsync(archivePath, new PackProgressReporter(progress), cancellationToken);
    public Task<LocalPackInspection> InspectLocalPackSelectionAsync(string archivePath, IReadOnlyList<string> optionalFiles,
        IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default, string? expectedArchiveSha256 = null)
        => InspectLocalPackCoreAsync(archivePath, progress is null ? null : new PackProgressReporter(progress), cancellationToken, optionalFiles.ToArray(), expectedArchiveSha256);
    private async Task<LocalPackInspection> InspectLocalPackCoreAsync(string archivePath, IProgress<PackInstallProgress>? progress, CancellationToken cancellationToken, IReadOnlyList<string>? optionalFiles = null, string? expectedArchiveSha256 = null)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var read = await new LocalPackArchive().ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (expectedArchiveSha256 is not null && !read.Inspection.ArchiveSha256.Equals(expectedArchiveSha256, StringComparison.OrdinalIgnoreCase)) optionalFiles = [];
        if (read.Inspection.Format != LocalPackFormat.CurseForge)
        {
            if (optionalFiles is { Count: > 0 } && (read.Inspection.Modrinth is not { } mr || optionalFiles.Distinct(StringComparer.Ordinal).Count() != optionalFiles.Count ||
                optionalFiles.Any(p => !mr.Files.Any(f => f.Path == p && f.Client == "optional")))) throw new InvalidDataException("可选文件选择已失效，请重新预检。");
            return optionalFiles is null ? read.Inspection : read.Inspection with { SelectedOptionalFiles = optionalFiles.ToArray() };
        }
        var effective = CurseForgePackSelection.Apply(read.CurseForgeFiles, optionalFiles);
        if (!read.CanResolveCurseForge) return read.Inspection;
        using var dedicated = localPackResolver is null ? new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false)) : null;
        var resolver = localPackResolver ?? new CurseForgePackResolver(dedicated!, build.CurseForgeApiKey, Paths.CacheDirectory, network);
        var identity = read.Inspection;
        var resolution = await resolver.ResolveAsync(effective, identity.GameVersion, identity.Loader, cancellationToken,
            new(identity.Name, identity.PackVersion ?? "", identity.GameVersion, identity.Loader, identity.LoaderVersion), progress).ConfigureAwait(false);
        var choices = await resolver.DescribeOptionalFilesAsync(read.CurseForgeFiles, cancellationToken).ConfigureAwait(false);
        var inspection = read.Inspection with { OptionalFiles = choices, SelectedOptionalFiles = (optionalFiles ?? []).ToArray() };
        var warnings = read.Inspection.Warnings.Concat(resolution.Warnings).Concat(resolution.BlockedFiles.Select(item => "未完成：" + item)).ToArray();
        if (resolution.BlockedFiles.Count > 0) return inspection with { Warnings = warnings, ManualFiles = resolution.ManualFiles,
            CapabilityReason = $"本次安装有 {resolution.BlockedFiles.Count} 项文件尚待补充或核实，未创建实例。" + (resolution.ManualFiles?.Count > 0 ? "请从官网获取指定文件，校验后继续安装。" : "请查看完整阻塞列表后重新预检。") };
        ValidateLocalPackTargets(read, resolution.Files);
        var key = CurseForgePackSelection.PlanKey(identity.ArchiveSha256, optionalFiles);
        cancellationToken.ThrowIfCancellationRequested(); localPackPlans[key] = new(resolution.Files.ToArray(), warnings);
        while (localPackPlans.Count > 8) { var other = localPackPlans.Keys.FirstOrDefault(k => k != key); if (other is null) break; localPackPlans.TryRemove(other, out _); }
        return inspection with { CanImport = true, CapabilityReason = null, Warnings = warnings, LocalFiles = resolution.Files.Select(f => f.Path).Concat(read.OverrideEntries.Keys).Distinct(StringComparer.Ordinal).ToArray() };
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
        var isLegacy = read.Inspection.LegacyProfile is not null && read.Inspection.CanImport;
        if (!isShou && !isLegacy && (read.Inspection.Format != LocalPackFormat.CurseForge || !read.CanResolveCurseForge)) throw new NotSupportedException(read.Inspection.CapabilityReason ?? "此格式尚未支持导入。");
        if ((isShou || isLegacy) && request.OptionalClientFiles is { Count: > 0 }) throw new InvalidDataException("SHOU 整包未声明可选文件。");
        if (!isShou && !isLegacy) _ = CurseForgePackSelection.Apply(read.CurseForgeFiles, request.OptionalClientFiles);
        var planKey = CurseForgePackSelection.PlanKey(request.ExpectedSha256, request.OptionalClientFiles);
        LocalPackPlan? plan = isShou || isLegacy ? new([], read.Inspection.Warnings) : null;
        if (!isShou && !isLegacy && !localPackPlans.TryGetValue(planKey, out plan))
        {
            var inspection = await InspectLocalPackCoreAsync(request.ArchivePath, progress, cancellationToken, request.OptionalClientFiles).ConfigureAwait(false);
            if (!inspection.CanImport || !localPackPlans.TryGetValue(planKey, out plan)) throw new InvalidOperationException(inspection.CapabilityReason ?? "整合包文件尚未全部核实。");
        }
        var capturedPlan = plan!; LocalPackImportResult? imported = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var root = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory; EnsureIsolatedGamePath(root); VerifiedDownloader.RejectLink(root); Directory.CreateDirectory(root);
            var stage = ContentInstallFiles.Literal(root, ".shou-local-stage-" + Guid.NewGuid().ToString("N")); var destination = ContentInstallFiles.Literal(root, Guid.NewGuid().ToString("N")); var moved = false; var registered = false;
            using var dedicated = localPackResolver is null ? new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false)) : null;
            var resolver = localPackResolver ?? new CurseForgePackResolver(dedicated!, build.CurseForgeApiKey, Paths.CacheDirectory, network);
            try
            {
                await RunContentTransferAsync("local-pack:" + Guid.NewGuid().ToString("N"), token => ImportLocalPackAsync(request, token), async token =>
                {
                    var inspection = isLegacy ? await new LocalPackArchive().StageLegacyAsync(request.ArchivePath, request.ExpectedSha256, stage, token, progress).ConfigureAwait(false)
                        : isShou ? await ShouPackArchive.StageAsync(request.ArchivePath, request.ExpectedSha256, stage, token).ConfigureAwait(false)
                        : await new LocalPackArchive().StageCurseForgeAsync(request.ArchivePath, request.ExpectedSha256, stage, capturedPlan.Files,
                        (file, path, transferToken) => resolver.DownloadAsync(file, path, new InlineProgress<TransferProgress>(OnTransfer), transferToken), token, progress, network.Capture().Downloads?.MaximumConnections ?? 8, request.OptionalClientFiles).ConfigureAwait(false);
                    var profile = await CacheImportedProfileAsync(inspection, stage, token).ConfigureAwait(false);
                    var instance = new GameInstance(Guid.NewGuid().ToString("N"), request.InstanceName.Trim(), profile, destination, MemoryMiB: inspection.RecommendedMemoryMiB, Loader: inspection.Loader, LoaderVersion: inspection.LoaderVersion, GameVersionId: inspection.GameVersion, LaunchOptions: inspection.LegacyProfile is { } legacy ? new GameLaunchOptions(InheritGlobal: false, JvmArguments: legacy.JvmArguments ?? "", GameArguments: legacy.GameArguments ?? "") : null);
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
