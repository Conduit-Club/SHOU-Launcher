using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IWorldDatapackManagementService
{
    private sealed record DatapackUpdatePlan(WorldDatapackUpdatePreview Preview, GameInstance Instance, string World, string Revision,
        IReadOnlyList<DatapackReplacement> Files, byte[]? Manifest, string RootName, DateTimeOffset Created);
    private readonly ConcurrentDictionary<string, DatapackUpdatePlan> datapackUpdates = new();
    public async Task<WorldDatapackOrderState> GetWorldDatapackOrderAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var store = WorldStore(instanceId, false); var state = await store.ReadDatapackWorkspaceAsync(worldDirectoryName, cancellationToken);
        if (state.Version != InstanceIdentity.GameVersion(FindInstance(instanceId))) throw new InvalidDataException("存档与实例版本不一致，未修改加载顺序。");
        return await store.GetDatapackOrderAsync(worldDirectoryName, cancellationToken).ConfigureAwait(false);
    }
    public async Task<WorldEditReceipt> SaveWorldDatapackOrderAsync(string instanceId, string worldDirectoryName, string expectedRevision, IReadOnlyList<WorldDatapackOrderItem> order, CancellationToken cancellationToken = default)
    {
        WorldEditReceipt? result = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var store = WorldStore(instanceId, true); var state = await store.ReadDatapackWorkspaceAsync(worldDirectoryName, ct);
            if (state.Version != InstanceIdentity.GameVersion(FindInstance(instanceId))) throw new InvalidDataException("存档与实例版本不一致。");
            result = await store.SaveDatapackOrderAsync(worldDirectoryName, expectedRevision, order, ct);
        }, cancellationToken);
        return result!;
    }
    private static string ManagedPackName(string packId) => packId.StartsWith("file/", StringComparison.Ordinal) ? WorldLeaf(packId[5..]) : throw new InvalidDataException("请选择本地文件数据包。");
    private async Task<ContentInstallManifest> WorldPackManifestAsync(GameInstance instance, string world, CancellationToken ct) =>
        await ContentInstallFiles.ReadManifestAsync(instance with { GameDirectory = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + WorldLeaf(world)) }, ct);
    private static ContentInstalledFile? TrackedPack(ContentInstallManifest manifest, string name) => manifest.Files.SingleOrDefault(f => f.Kind == "datapack" && f.RelativePath == "datapacks/" + name);
    private async Task<bool> MatchesTrackedPackAsync(GameInstance instance, string world, ContentInstalledFile file, CancellationToken ct)
    {
        var root = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + WorldLeaf(world));
        var bytes = await WorldContentArchive.ReadFileAsync(root, ContentInstallFiles.Literal(root, file.RelativePath), 64 * 1024 * 1024, ct);
        return bytes.LongLength == file.Size && Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) && Convert.ToHexStringLower(SHA512.HashData(bytes)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase);
    }
    public async Task<WorldDatapackUpdateOptions> GetWorldDatapackUpdateOptionsAsync(string instanceId, string worldDirectoryName, string packId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); var instance = ContentTarget(instanceId, false); var store = WorldStore(instanceId, false);
        var state = await store.ReadDatapackWorkspaceAsync(worldDirectoryName, cancellationToken); var name = ManagedPackName(packId);
        var current = state.Packs.GetValueOrDefault(name) ?? throw new IOException("数据包文件已缺失，请重新读取。");
        var manifest = await WorldPackManifestAsync(instance, worldDirectoryName, cancellationToken); var tracked = TrackedPack(manifest, name);
        if (current.Directory || tracked is null || !await MatchesTrackedPackAsync(instance, worldDirectoryName, tracked, cancellationToken))
            return new(state.Revision, new(packId, current.Hash), name, "本地来源；可选择 ZIP 替换", null, null, []);
        if (manifest.Files.Count(f => f.ProjectId == tracked.ProjectId) != 1) throw new InvalidDataException("此项目有多份安装记录，请先明确处理，未猜测替换范围。");
        var catalog = worldContentCatalog as ILauncherCatalogService ?? this;
        IReadOnlyList<ContentRelease> releases; string origin;
        try
        {
            var identities = worldContentCatalog ?? this; var project = await identities.GetContentProjectAsync(tracked.ProjectId, cancellationToken);
            var currentRelease = await identities.GetContentReleaseAsync(tracked.ReleaseId, cancellationToken);
            if (project.Id != tracked.ProjectId || currentRelease.ProjectId != tracked.ProjectId) throw new InvalidDataException("来源身份已改变。");
            origin = project.Title + " · " + currentRelease.Version;
            releases = await catalog.GetContentReleasesAsync(tracked.ProjectId, state.Version, tracked.ProjectId.StartsWith("cf-", StringComparison.Ordinal) ? null : "datapack", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        { return new(state.Revision, new(packId, current.Hash), name, "在线来源暂不可用", tracked.ProjectId, tracked.ReleaseId, [], "在线版本读取失败，可重试或选择本地 ZIP。"); }
        var choices = releases.Where(r => r.ProjectId == tracked.ProjectId && r.Id != tracked.ReleaseId && r.GameVersions.Contains(state.Version, StringComparer.Ordinal))
            .OrderByDescending(r => r.Published).SelectMany(r => r.Files.Where(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).Select(f => new WorldDatapackUpdateChoice(r.Id, r.Version, r.Name, f.FileName, r.Published))).Take(200).ToArray();
        return new(state.Revision, new(packId, current.Hash), name, origin, tracked.ProjectId, tracked.ReleaseId, choices);
    }
    public async Task<WorldDatapackUpdatePreview> PreviewWorldDatapackUpdateAsync(WorldDatapackUpdateRequest request, CancellationToken cancellationToken = default)
    {
        WorldLeaf(request.WorldDirectoryName); var oldName = ManagedPackName(request.Selection.PackId); WorldDatapackUpdatePreview? result = null;
        await WorldContentOperationAsync(request.InstanceId, ct => PreviewWorldDatapackUpdateAsync(request, ct), async ct =>
        {
            var instance = ContentTarget(request.InstanceId, true); var store = WorldStore(request.InstanceId, true);
            var state = await store.ReadDatapackWorkspaceAsync(request.WorldDirectoryName, ct);
            if (state.Revision != request.WorkspaceRevision || !state.Packs.TryGetValue(oldName, out var selected) || selected.Hash != request.Selection.ContentRevision) throw new IOException("存档、数据包或安装清单已改变，请重新检查。");
            var game = await WorldGameAsync(instance, ct);
            if (state.Version != game.Version || state.DataVersion != game.DataVersion || state.Format != game.PackFormat) throw new InvalidDataException("世界与目标实例的版本/格式不一致。");
            var manifest = await WorldPackManifestAsync(instance, request.WorldDirectoryName, ct); var catalog = worldContentCatalog ?? this;
            var files = new List<DatapackReplacement>(); var warnings = new List<string>(); var nextTracked = new List<ContentInstalledFile>(); var removedProjects = new HashSet<string>(StringComparer.Ordinal);
            var changes = new List<string>(); string rootName; long downloaded = 0;
            if (request.LocalArchivePath is { } local)
            {
                if (request.ReleaseId is not null || !Path.IsPathFullyQualified(local) || !Path.GetExtension(local).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("手动更新须明确选择单个本地 ZIP。");
                var incoming = await WorldContentArchive.DatapackAsync(local, game.PackFormat, ct); rootName = WorldLeaf(Path.GetFileName(local));
                files.Add(new(oldName, rootName, local, incoming.Revision));
                var tracked = TrackedPack(manifest, oldName); if (tracked is not null) removedProjects.Add(tracked.ProjectId);
                warnings.Add("手动替换无法核实在线项目身份；原来源记录随旧包备份，新包按本地来源管理。");
                changes.Add(oldName + " → " + rootName);
            }
            else
            {
                var tracked = TrackedPack(manifest, oldName) ?? throw new InvalidOperationException("没有精确在线来源，请选择本地 ZIP 替换。");
                if (selected.Directory || !await MatchesTrackedPackAsync(instance, request.WorldDirectoryName, tracked, ct)) throw new IOException("原文件与在线安装记录不一致，请重新核对或选择手动替换。");
                if (request.ReleaseId is null || request.FileName is null) throw new InvalidDataException("请选择明确版本及文件。");
                var root = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + request.WorldDirectoryName);
                var listings = catalog as ILauncherCatalogService ?? this;
                var plan = await new ContentInstallPlanner(listings, catalog).PlanAsync(instance with { GameDirectory = root }, state.Version,
                    new(instance.Id, tracked.ProjectId, request.ReleaseId, request.FileName, "datapack"), ct, worldDatapacks: true, updatingDatapacks: true);
                foreach (var file in plan.Files)
                {
                    var installed = manifest.Files.Where(f => f.ProjectId == file.ProjectId).ToArray();
                    if (installed.Length > 1 || installed.Any(f => f.Kind != "datapack" || !f.RelativePath.StartsWith("datapacks/", StringComparison.Ordinal) || f.RelativePath[10..].Contains('/'))) throw new InvalidDataException("安装来源存在多文件或非数据包结构，未猜测替换范围。");
                    var oldFile = installed.SingleOrDefault();
                    if (oldFile is not null && !await MatchesTrackedPackAsync(instance, request.WorldDirectoryName, oldFile, ct)) throw new IOException("原包或依赖与安装记录不一致，未替换。");
                    if (file.File.Size > 64 * 1024 * 1024 || (downloaded += file.File.Size) > 128L * 1024 * 1024) throw new IOException("本次更新 ZIP 超过读取预算。");
                    var path = DownloadArchive(Paths, file.File); await DownloadInstallFileAsync(file.File, path, ct);
                    var inspected = await WorldContentArchive.DatapackAsync(path, state.Format, ct); string sha512;
                    using (var zip = PackArchiveReader.Open(path)) { var bytes = await WorldContentArchive.ReadFileAsync(Path.GetDirectoryName(path)!, path, 64 * 1024 * 1024, ct); sha512 = Convert.ToHexStringLower(SHA512.HashData(bytes)); }
                    var previousName = oldFile?.RelativePath[10..]; var enable = file.Dependency && state.Enabled.Contains(request.Selection.PackId, StringComparer.Ordinal);
                    if (enable && state.Disabled.Contains("file/" + (previousName ?? file.File.FileName), StringComparer.Ordinal)) throw new InvalidOperationException("必需依赖被明确禁用，请先核对启用状态。");
                    files.Add(new(previousName, file.File.FileName, path, inspected.Revision, enable)); removedProjects.Add(file.ProjectId);
                    nextTracked.Add(new(file.ProjectId, file.ReleaseId, "datapack", file.RelativePath, file.File.Sha1, sha512, file.File.Size, file.File.Uri.AbsoluteUri));
                    changes.Add((previousName ?? "新增依赖") + " → " + file.File.FileName + (enable && !state.Enabled.Contains("file/" + (previousName ?? file.File.FileName), StringComparer.Ordinal) ? "（启用必需依赖）" : ""));
                }
                rootName = request.FileName; warnings.AddRange(plan.Warnings);
            }
            var replacedNames = files.Where(f => f.OldName is not null).Select(f => f.OldName!).ToHashSet(StringComparer.Ordinal);
            if (files.Select(f => ContentInstallFiles.PathKey(f.NewName)).Distinct().Count() != files.Count) throw new IOException("更新文件名称发生冲突。");
            foreach (var file in files)
                if (state.Packs.Keys.Any(name => ContentInstallFiles.PathKey(name) == ContentInstallFiles.PathKey(file.NewName) && !replacedNames.Contains(name))) throw new IOException("新文件名与其它数据包冲突：" + file.NewName);
            await CheckDatapackUpdateDependenciesAsync(instance, request.WorldDirectoryName, manifest, removedProjects, nextTracked, catalog, ct);
            var resources = new Dictionary<string, (PackResource, string)>(); long expanded = 0;
            foreach (var pack in state.Packs.Values.Where(p => !replacedNames.Contains(p.Name)))
            {
                var path = ContentInstallFiles.Literal(instance.GameDirectory, "saves/" + request.WorldDirectoryName + "/datapacks/" + pack.Name);
                var inspected = pack.Directory ? await WorldContentArchive.FolderDatapackAsync(path, state.Format, ct) : await WorldContentArchive.DatapackAsync(path, state.Format, ct);
                expanded += inspected.ExpandedBytes;
                foreach (var (key, value) in inspected.Resources) resources.TryAdd(key, (value, pack.Name));
            }
            foreach (var file in files)
            {
                var inspected = await WorldContentArchive.DatapackAsync(file.Source, state.Format, ct); expanded += inspected.ExpandedBytes;
                WorldContentArchive.CheckResources(resources, inspected, file.NewName, warnings);
                if (expanded > 256L * 1024 * 1024 || resources.Count > 50000) throw new IOException("更新后的数据包资源超过扫描预算。");
            }
            var nextManifest = MergeDatapackUpdateManifest(state.Manifest, files, nextTracked);
            if ((await store.ReadDatapackWorkspaceAsync(request.WorldDirectoryName, ct)).Revision != state.Revision) throw new IOException("数据包在下载/检查期间改变，请重新读取。");
            var preview = new WorldDatapackUpdatePreview(Guid.NewGuid().ToString("N"), instance.Name, request.WorldDirectoryName, changes, warnings.Distinct().ToArray());
            datapackUpdates[preview.Id] = new(preview, instance, request.WorldDirectoryName, state.Revision, files, nextManifest, rootName, DateTimeOffset.UtcNow);
            foreach (var id in datapackUpdates.Keys.Where(k => k != preview.Id).Take(Math.Max(0, datapackUpdates.Count - 8))) datapackUpdates.TryRemove(id, out _);
            result = preview;
        }, cancellationToken);
        return result!;
    }
    private async Task CheckDatapackUpdateDependenciesAsync(GameInstance instance, string world, ContentInstallManifest manifest, IReadOnlySet<string> removed, IReadOnlyList<ContentInstalledFile> incoming, IContentDependencyCatalogService catalog, CancellationToken ct)
    {
        var retained = manifest.Files.Where(f => !removed.Contains(f.ProjectId)).ToArray();
        var all = retained.Concat(incoming).ToArray(); var projects = all.GroupBy(f => f.ProjectId).ToArray();
        if (projects.Length > 64 || projects.Any(p => p.Select(f => f.ReleaseId).Distinct().Count() != 1)) throw new InvalidDataException("来源项目过多或同项目存在不同版本。");
        foreach (var group in projects)
        {
            foreach (var file in retained.Where(f => f.ProjectId == group.Key)) if (!await MatchesTrackedPackAsync(instance, world, file, ct)) throw new IOException("其它已登记数据包与记录不一致，未猜测依赖关系。");
            var release = await catalog.GetContentReleaseAsync(group.First().ReleaseId, ct);
            if (release.ProjectId != group.Key) throw new InvalidDataException("依赖来源身份发生变化。");
            foreach (var dependency in release.Dependencies ?? [])
            {
                var matches = dependency.VersionId is { } version ? all.Any(f => f.ReleaseId == version) : dependency.ProjectId is { } project && all.Any(f => f.ProjectId == project);
                if (dependency.Type == "required" && !matches) throw new InvalidDataException("更新会破坏其它数据包的必需依赖或固定版本：" + group.Key);
                if (dependency.Type == "incompatible" && matches) throw new InvalidDataException("更新后的数据包声明了不兼容关系：" + group.Key);
                if (dependency.Type is not ("required" or "incompatible" or "optional" or "embedded")) throw new InvalidDataException("未知依赖类型，未更新。");
            }
        }
    }
    private static byte[]? MergeDatapackUpdateManifest(byte[]? original, IReadOnlyList<DatapackReplacement> files, IReadOnlyList<ContentInstalledFile> incoming)
    {
        if (original is null) { if (incoming.Count != 0) throw new InvalidDataException("缺少原在线来源清单。"); return null; }
        var json = JsonNode.Parse(original, documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException("安装清单无效。");
        if (json["Files"] is not JsonArray records) throw new InvalidDataException("安装清单缺少 Files。");
        var retained = new JsonArray(); var removed = new List<JsonObject>();
        foreach (var node in records)
        {
            var obj = node as JsonObject ?? throw new InvalidDataException("安装记录无效。"); var path = (string?)obj["RelativePath"] ?? throw new InvalidDataException("安装记录缺少路径。");
            if (files.Any(f => f.OldName is { } old && (ContentInstallFiles.PathKey(path) == ContentInstallFiles.PathKey("datapacks/" + old) || path.StartsWith("datapacks/" + old + "/", StringComparison.Ordinal)))) removed.Add(obj);
            else retained.Add(obj.DeepClone());
        }
        foreach (var file in incoming)
        {
            var record = removed.FirstOrDefault(o => (string?)o["ProjectId"] == file.ProjectId)?.DeepClone() as JsonObject ?? new JsonObject();
            var known = JsonSerializer.SerializeToNode(file)!.AsObject(); foreach (var field in known) record[field.Key] = field.Value?.DeepClone(); retained.Add(record);
        }
        json["Files"] = retained; return JsonSerializer.SerializeToUtf8Bytes(json);
    }
    public async Task<WorldDatapackUpdateReceipt> ApplyWorldDatapackUpdateAsync(string previewId, CancellationToken cancellationToken = default)
    {
        if (!datapackUpdates.TryGetValue(previewId, out var plan) || DateTimeOffset.UtcNow - plan.Created > TimeSpan.FromMinutes(30)) throw new InvalidOperationException("更新预检已失效，请重新检查。");
        WorldDatapackUpdateReceipt? result = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var instance = ContentTarget(plan.Instance.Id, true);
            if (instance.GameDirectory != plan.Instance.GameDirectory || InstanceIdentity.GameVersion(instance) != InstanceIdentity.GameVersion(plan.Instance)) throw new IOException("目标实例已改变。");
            result = await WorldStore(instance.Id, true).ApplyDatapackUpdateAsync(plan.World, plan.Revision, plan.Files, plan.Manifest, plan.RootName, ct);
            datapackUpdates.TryRemove(previewId, out _);
        }, cancellationToken);
        return result!;
    }
    public Task DiscardWorldDatapackUpdateAsync(string previewId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); datapackUpdates.TryRemove(previewId, out _); return Task.CompletedTask; }
}
