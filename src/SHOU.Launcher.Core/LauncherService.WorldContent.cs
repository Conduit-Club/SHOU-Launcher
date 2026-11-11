using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IWorldContentInstallationService
{
    private readonly IContentDependencyCatalogService? worldContentCatalog;
    private readonly ConcurrentDictionary<string, WorldDownloadPlan> worldDownloads = new();
    private sealed record WorldGameIdentity(string Version, int DataVersion, DataPackFormat PackFormat);
    private sealed record WorldDownloadPlan(WorldContentPreview Preview, WorldContentRequest Request, GameInstance Instance,
        WorldGameIdentity Game, ContentInstallPlan? Datapacks, InspectedWorld? World, IReadOnlyDictionary<string, string> Archives,
        IReadOnlyDictionary<string, string> ArchiveHashes,
        string Revision, WorldPathSnapshot TargetGuard, IReadOnlySet<string> NewPacks, DateTimeOffset Created, LocalWorldSource? LocalSource = null);
    private sealed record WorldPackSnapshot(string Revision, IReadOnlyDictionary<string, InspectedDatapack> Packs);
    private Task WorldContentOperationAsync(string identity, Func<CancellationToken, Task> retry, Func<CancellationToken, Task> action, CancellationToken ct) =>
        ExecuteAsync("Installing", token => RunContentTransferAsync("world-content:" + identity, retry, action, token), ct);
    private static string WorldLeaf(string value)
    {
        ModrinthPackArchive.SafeRelative(value);
        if (value.Length is < 1 or > 120 || value.Contains('/') || value.StartsWith('.') || value.Any(c => "<>:\"/\\|?*".Contains(c)) || value.EndsWith('.') || value.EndsWith(' ')) throw new InvalidDataException("请输入安全的存档文件夹名。");
        return value;
    }
    public async Task<IReadOnlyList<WorldContentTarget>> GetWorldContentTargetsAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return (await WorldStore(ContentTarget(instanceId, false)).GetWorldsAsync(cancellationToken).ConfigureAwait(false))
            .Select(w => new WorldContentTarget(w.DirectoryName, w.Name, w.Version, w.DataVersion, w.Error)).ToArray();
    }
    private async Task<WorldGameIdentity> WorldGameAsync(GameInstance instance, CancellationToken ct)
    {
        var game = InstanceIdentity.GameVersion(instance);
        var metadata = await new VersionMetadataResolver(http, downloader, Paths.CacheDirectory, Sources).ResolveAsync(game, ct).ConfigureAwait(false);
        var client = metadata.Metadata["downloads"]?["client"] ?? throw new InvalidDataException("缺少游戏版本校验信息，请先校验实例文件。");
        var sha1 = (string?)client["sha1"]; var size = (long?)client["size"];
        if (!ContentInstallFiles.Hash(sha1, 40) || size is null or <= 0 or > 256 * 1024 * 1024) throw new InvalidDataException("基础版本文件缺少可信哈希或大小。");
        var path = ContentInstallFiles.Literal(Paths.CacheDirectory, $"versions/{game}/{game}.jar");
        await downloader.DownloadAsync(new(PCL.Core.Conduit.DownloadSources.Resolve([(string)client["url"]!], Sources()).Select(s => new Uri(s.Url)).ToArray(), path, sha1, Size: size), new InlineProgress<TransferProgress>(OnTransfer), ct).ConfigureAwait(false);
        if (!await ContentInstallFiles.MatchesAsync(path, sha1!, size.Value, ct).ConfigureAwait(false)) throw new InvalidDataException("基础版本校验失败。");
        var guard = WorldPathSnapshot.Capture(Paths.CacheDirectory, path);
        using var zip = ZipFile.OpenRead(path); var entry = zip.GetEntry("version.json") ?? throw new NotSupportedException("此历史版本没有可核验的世界/数据包格式信息。");
        using var data = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(entry, 65536, ct)); var info = data.RootElement;
        if (info.GetProperty("id").GetString() != game) throw new InvalidDataException("游戏内版本身份不一致。");
        var dataVersion = info.GetProperty("world_version").GetInt32(); var packs = info.GetProperty("pack_version");
        var format = packs.TryGetProperty("data", out var legacy) ? new DataPackFormat(legacy.GetInt32(), 0) : new(packs.GetProperty("data_major").GetInt32(), packs.GetProperty("data_minor").GetInt32());
        if (dataVersion <= 0 || format.Major <= 0 || format.Minor < 0) throw new InvalidDataException("游戏格式号无效。");
        guard.Verify(); return new(game, dataVersion, format);
    }
    private static string DownloadArchive(IAppPaths paths, ContentFile file) => ContentInstallFiles.Literal(paths.CacheDirectory, "content/" + file.Sha1.ToLowerInvariant() + "/" + WorldLeaf(file.FileName));
    public async Task<WorldContentPreview> PreviewWorldContentAsync(WorldContentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Kind is not ("datapack" or "world")) throw new ArgumentException("请选择数据包或世界资源。");
        WorldLeaf(request.WorldDirectoryName); WorldLeaf(request.FileName);
        WorldContentPreview? result = null;
        await WorldContentOperationAsync(request.InstanceId, token => PreviewWorldContentAsync(request, token), async ct =>
        {
            var instance = ContentTarget(request.InstanceId, true); var catalog = worldContentCatalog ?? this;
            var game = await WorldGameAsync(instance, ct).ConfigureAwait(false);
            var root = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + request.WorldDirectoryName);
            var archives = new Dictionary<string, string>(); var archiveHashes = new Dictionary<string, string>(); ContentInstallPlan? packs = null; InspectedWorld? world = null;
            var warnings = new List<string>(); string revision; WorldPathSnapshot targetGuard; var newPacks = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<string> files;
            if (request.Kind == "datapack")
            {
                var targetWorld = (await WorldStore(instance).GetWorldsAsync(ct)).SingleOrDefault(w => w.DirectoryName == request.WorldDirectoryName)
                    ?? throw new InvalidDataException("请选择已存在的 Java 存档。");
                if (targetWorld.Error is not null || targetWorld.Version != game.Version || targetWorld.DataVersion != game.DataVersion) throw new InvalidDataException("存档与实例版本不一致或未知；不会自动升级/降级存档。");
                targetGuard = WorldPathSnapshot.Capture(instance.GameDirectory, root);
                var destination = instance with { GameDirectory = root };
                packs = await new ContentInstallPlanner(this, catalog).PlanAsync(destination, targetWorld.Version, new(request.InstanceId, request.ProjectId, request.ReleaseId, request.FileName, "datapack"), ct, worldDatapacks: true);
                var existing = await SnapshotWorldPacksAsync(instance, request.WorldDirectoryName, game, null, ct);
                revision = existing.Revision; var known = new Dictionary<string, (PackResource, string)>();
                foreach (var (name, pack) in existing.Packs) WorldContentArchive.CheckResources(known, pack, name, warnings);
                foreach (var file in packs.Files)
                {
                    ContentInstallFiles.ValidateFile(file.File);
                    if (file.File.Size > 64 * 1024 * 1024) throw new InvalidDataException("数据包超过 64 MiB 限制。");
                    var path = DownloadArchive(Paths, file.File); archives[file.RelativePath] = path;
                    await DownloadInstallFileAsync(file.File, path, ct).ConfigureAwait(false);
                    var pack = await WorldContentArchive.DatapackAsync(path, game.PackFormat, ct);
                    archiveHashes[file.RelativePath] = pack.Revision;
                    WorldContentArchive.CheckResources(known, pack, file.File.FileName, warnings);
                    if (!File.Exists(ContentInstallFiles.Literal(root, file.RelativePath))) newPacks.Add(file.RelativePath["datapacks/".Length..]);
                }
                files = packs.Files.Select(f => f.RelativePath).ToArray(); warnings.AddRange(packs.Warnings);
                warnings.Add("安装不更改当前启停状态。");
            }
            else
            {
                RequireMissingWorld(instance, request.WorldDirectoryName);
                targetGuard = WorldPathSnapshot.Capture(instance.GameDirectory, ContentInstallFiles.Literal(instance.GameDirectory, "saves"), true);
                var project = await catalog.GetContentProjectAsync(request.ProjectId, ct); var release = await catalog.GetContentReleaseAsync(request.ReleaseId, ct);
                if (project.Id != request.ProjectId || project.Kind != "world" || release.Id != request.ReleaseId || release.ProjectId != project.Id || !release.GameVersions.Contains(game.Version, StringComparer.Ordinal)) throw new InvalidDataException("世界资源身份或目标版本不兼容。");
                if (release.Loaders.Count > 0 && !release.Loaders.Contains(instance.Loader, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("此世界声明的加载器与目标实例不一致。");
                if (release.Dependencies?.Any(d => d.Type is "required" or "incompatible") == true) throw new NotSupportedException("此世界声明额外必装/冲突依赖，请先选择可独立安装的世界文件。");
                var file = release.Files.SingleOrDefault(f => f.FileName == request.FileName) ?? throw new InvalidDataException("请选择明确的世界文件。");
                ContentInstallFiles.ValidateFile(file);
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Size > PackArchiveReader.MaximumArchiveBytes) throw new InvalidDataException("世界须为不超过 256 MiB 的 Java ZIP。");
                var path = DownloadArchive(Paths, file); await DownloadInstallFileAsync(file, path, ct); archives["world"] = path;
                world = await WorldContentArchive.WorldAsync(path, ct);
                if (world.DataVersion != game.DataVersion || world.Version != game.Version) throw new InvalidDataException("ZIP 内世界版本与所选实例不一致，不自动升级/降级世界。");
                using var archive = PackArchiveReader.Open(path); revision = await archive.Sha256Async(ct); files = world.Files;
                var inspect = ContentInstallFiles.Literal(Paths.TempDirectory, "world-inspect-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(inspect);
                    var metadataFiles = world.Files.Where(p => p == "level.dat" || p.StartsWith("datapacks/", StringComparison.Ordinal)).ToArray();
                    await WorldContentArchive.ExtractWorldAsync(path, inspect, world with { Files = metadataFiles }, revision, ct);
                    await ValidateWorldPacksForInstanceAsync(inspect, game.PackFormat, instance, ct);
                }
                finally { VerifiedDownloader.RejectLink(inspect); if (Directory.Exists(inspect)) Directory.Delete(inspect, true); }
                warnings.Add("导入为新的存档文件夹，保留 ZIP 内世界数据，不覆盖已有存档。");
            }
            ct.ThrowIfCancellationRequested();
            var preview = new WorldContentPreview(Guid.NewGuid().ToString("N"), request.Kind, instance.Name, request.WorldDirectoryName, game.Version, files.ToArray(), warnings.Distinct().ToArray());
            worldDownloads[preview.Id] = new(preview, request, instance, game, packs, world, archives, archiveHashes, revision, targetGuard, newPacks, DateTimeOffset.UtcNow);
            foreach (var key in worldDownloads.Keys.Where(k => k != preview.Id).Take(Math.Max(0, worldDownloads.Count - 8))) worldDownloads.TryRemove(key, out _);
            result = preview;
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    private static void RequireMissingWorld(GameInstance instance, string leaf)
    {
        var saves = ContentInstallFiles.Literal(instance.GameDirectory, "saves");
        if (Directory.Exists(saves) && Directory.EnumerateFileSystemEntries(saves).Any(p => ContentInstallFiles.PathKey(Path.GetFileName(p)) == ContentInstallFiles.PathKey(leaf))) throw new IOException("存档文件夹已存在或名称冲突，请换一个新名称。");
        if (File.Exists(saves)) throw new IOException("saves 不是目录。");
    }
    private async Task<WorldPackSnapshot> SnapshotWorldPacksAsync(GameInstance instance, string world, WorldGameIdentity expected, IReadOnlySet<string>? ignore, CancellationToken ct)
    {
        var root = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + world);
        var level = await WorldContentArchive.ReadFileAsync(instance.GameDirectory, ContentInstallFiles.Literal(root, "level.dat"), new NbtLimits().MaxInputBytes, ct);
        var data = BoundedNbt.Read(level, cancellationToken: ct).Root.Find("Data")!;
        if (data.Find("DataVersion") is not { Type: NbtType.Int } number || (int)number.Value != expected.DataVersion || data.Find("Version")?.Find("Name") is not { Type: NbtType.String } name || (string)name.Value != expected.Version) throw new IOException("目标世界版本已改变，请重新预检。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(SHA256.HashData(level));
        var marker = ContentInstallFiles.Literal(root, ContentInstallFiles.ManifestName);
        if (File.Exists(marker)) hash.AppendData(SHA256.HashData(await WorldContentArchive.ReadFileAsync(instance.GameDirectory, marker, 4 * 1024 * 1024, ct)));
        var result = new Dictionary<string, InspectedDatapack>(StringComparer.Ordinal); var keys = new HashSet<string>(); long totalBytes = 0; var resources = 0;
        var folder = ContentInstallFiles.Literal(root, "datapacks");
        if (Directory.Exists(folder))
        {
            var paths = Directory.EnumerateFileSystemEntries(folder).Take(1025).Order(StringComparer.Ordinal).ToArray(); if (paths.Length > 1024) throw new IOException("目标数据包超过 1024 项。");
            foreach (var path in paths)
            {
                var leaf = Path.GetFileName(path); _ = ContentInstallFiles.Literal(folder, leaf);
                if (ignore?.Contains(leaf) == true) continue;
                if (!keys.Add(ContentInstallFiles.PathKey(leaf))) throw new IOException("已有数据包存在大小写/Unicode 重名。");
                InspectedDatapack pack;
                if (Directory.Exists(path)) pack = await WorldContentArchive.FolderDatapackAsync(path, expected.PackFormat, ct);
                else if (leaf.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) pack = await WorldContentArchive.DatapackAsync(path, expected.PackFormat, ct);
                else { hash.AppendData(Encoding.UTF8.GetBytes(leaf)); hash.AppendData(SHA256.HashData(await WorldContentArchive.ReadFileAsync(instance.GameDirectory, path, 64 * 1024 * 1024, ct))); continue; }
                totalBytes += pack.ExpandedBytes; resources += pack.Resources.Count;
                if (totalBytes > 256 * 1024 * 1024 || resources > 50000) throw new IOException("目标数据包总量超过安全扫描预算。");
                result.Add(leaf, pack); hash.AppendData(Encoding.UTF8.GetBytes(leaf + "\0" + pack.Revision));
            }
        }
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), result);
    }
    public async Task<WorldContentResult> InstallWorldContentAsync(string previewId, CancellationToken cancellationToken = default)
    {
        if (!worldDownloads.TryGetValue(previewId, out var plan) || DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(30)) throw new InvalidOperationException("安装预检已过期，请重新读取。");
        WorldContentResult? result = null;
        await WorldContentOperationAsync(plan.Request.InstanceId, token => InstallWorldContentAsync(previewId, token), async ct =>
        {
            var instance = ContentTarget(plan.Request.InstanceId, true);
            if (instance.GameDirectory != plan.Instance.GameDirectory || InstanceIdentity.GameVersion(instance) != plan.Game.Version) throw new IOException("目标实例已改变，请重新预检。");
            await VerifyLocalWorldSourceAsync(plan, ct);
            var root = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + plan.Request.WorldDirectoryName);
            if (plan.Datapacks is { } packs)
            {
                async Task Validate(bool published, CancellationToken token)
                {
                    EnsureInstanceNotRunning(instance); plan.TargetGuard.Verify();
                    if ((await SnapshotWorldPacksAsync(instance, plan.Request.WorldDirectoryName, plan.Game, published ? plan.NewPacks : null, token)).Revision != plan.Revision) throw new IOException("存档或已有数据包自预检后改变，请重新读取。");
                }
                await Validate(false, ct);
                var installed = await ContentInstallFiles.InstallAsync(instance with { GameDirectory = root }, packs, async (file, target, token) =>
                {
                    var source = plan.Archives["datapacks/" + file.FileName];
                    if (!await ContentInstallFiles.MatchesAsync(source, file.Sha1, file.Size, token)) throw new IOException("缓存数据包已改变，请重新预检。");
                    using var zip = PackArchiveReader.Open(source);
                    if (await zip.Sha256Async(token) != plan.ArchiveHashes["datapacks/" + file.FileName]) throw new IOException("缓存数据包与预检身份不同。");
                    await zip.CopyToAsync(target, token);
                    _ = await WorldContentArchive.DatapackAsync(target, plan.Game.PackFormat, token);
                }, ct, Validate);
                result = new(instance.Id, plan.Request.WorldDirectoryName, installed.AddedFiles, root);
            }
            else
            {
                EnsureInstanceNotRunning(instance); plan.TargetGuard.VerifyExistingNodes(); RequireMissingWorld(instance, plan.Request.WorldDirectoryName);
                var stage = ContentInstallFiles.Literal(instance.GameDirectory, ".shou-world-install-" + Guid.NewGuid().ToString("N"));
                var saves = ContentInstallFiles.Literal(instance.GameDirectory, "saves"); var createdSaves = false;
                try
                {
                    var archive = plan.Archives["world"]; using (var zip = PackArchiveReader.Open(archive)) if (await zip.Sha256Async(ct) != plan.Revision) throw new IOException("世界 ZIP 自预检后改变。");
                    Directory.CreateDirectory(stage); await WorldContentArchive.ExtractWorldAsync(archive, stage, plan.World!, plan.Revision, ct);
                    await ValidateWorldPacksForInstanceAsync(stage, plan.Game.PackFormat, plan.Instance, ct);
                    var sourceRecord = plan.LocalSource is null
                        ? JsonSerializer.Serialize(new { plan.Request.ProjectId, plan.Request.ReleaseId, plan.Request.FileName, Sha256 = plan.Revision })
                        : JsonSerializer.Serialize(new { Kind = "local-world", plan.Request.FileName, Sha256 = plan.Revision, GameVersion = plan.Game.Version });
                    await File.WriteAllTextAsync(ContentInstallFiles.Literal(stage, ".shou-world-source.json"), sourceRecord, ct);
                    await VerifyLocalWorldSourceAsync(plan, ct);
                    ct.ThrowIfCancellationRequested(); plan.TargetGuard.VerifyExistingNodes(); EnsureInstanceNotRunning(instance); RequireMissingWorld(instance, plan.Request.WorldDirectoryName);
                    if (!Directory.Exists(saves)) { Directory.CreateDirectory(saves); createdSaves = true; }
                    _ = ContentInstallFiles.Literal(instance.GameDirectory, "saves");
                    await DirectoryPublication.PublishAsync(stage, root, ct);
                    result = new(instance.Id, plan.Request.WorldDirectoryName, plan.World!.Files.Count, root);
                }
                finally
                {
                    VerifiedDownloader.RejectLink(stage); if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    if (createdSaves) { VerifiedDownloader.RejectLink(saves); if (Directory.Exists(saves) && !Directory.EnumerateFileSystemEntries(saves).Any()) Directory.Delete(saves, false); }
                }
            }
            worldDownloads.TryRemove(previewId, out _);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
