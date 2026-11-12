using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Content;

public sealed class ContentInstallPlanner(ILauncherCatalogService catalog, IContentDependencyCatalogService dependencies)
{
    public const int MaximumProjects = 64, MaximumDepth = 16;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public async Task<ContentInstallPlan> PlanAsync(GameInstance instance, string gameVersion, ContentInstallRequest request, CancellationToken ct = default, bool worldDatapacks = false, bool updatingDatapacks = false,
        IReadOnlySet<string>? replacingModPaths = null, ContentInstallManifest? installedMods = null, string? replacingResourcePath = null)
    {
        if (replacingResourcePath is not null && (request.Kind is not ("resourcepack" or "shader") || replacingModPaths is not null || worldDatapacks || updatingDatapacks ||
            !replacingResourcePath.StartsWith(Folder(request.Kind) + "/", StringComparison.Ordinal) || replacingResourcePath[(Folder(request.Kind).Length + 1)..].Contains('/')))
            throw new InvalidDataException("资源更新须明确当前类别的一个原文件。");
        if (replacingModPaths is not null && (request.Kind != "mod" || worldDatapacks || updatingDatapacks || replacingModPaths.Count != 1)) throw new InvalidOperationException("模组更新须明确一个原文件。");
        if (updatingDatapacks && !worldDatapacks) throw new InvalidOperationException("替换模式仅用于显式存档数据包更新预检。");
        if (request.Kind == "datapack" && !worldDatapacks) throw new NotSupportedException("数据包须明确选择存档目标。");
        if (worldDatapacks && request.Kind != "datapack") throw new InvalidDataException("存档安装只接受数据包。");
        if (instance.Id != request.InstanceId) throw new InvalidDataException("安装实例身份不一致。");
        SafePath.Identifier(request.ProjectId); SafePath.Identifier(request.ReleaseId); SafePath.Identifier(gameVersion);
        var files = new List<ContentInstallFile>(); var warnings = new List<string>();
        var resolved = new Dictionary<string, ContentRelease>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var incompatibilities = new List<ContentDependency>(); long total = 0;
        async Task Visit(ContentRelease release, string? expectedProject, string? explicitFile, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > MaximumDepth || resolved.Count + active.Count >= MaximumProjects) throw new InvalidDataException("required 依赖超过64个项目或16层限制。");
            var projectId = release.ProjectId ?? throw new InvalidDataException("资源版本缺少项目身份。");
            SafePath.Identifier(projectId);
            if (expectedProject is not null && expectedProject != projectId) throw new InvalidDataException("依赖版本不属于声明项目。");
            if (active.Contains(projectId)) throw new InvalidDataException("required 依赖存在循环，未安装任何文件。");
            if (resolved.TryGetValue(projectId, out var previous))
            {
                if (previous.Id != release.Id) throw new InvalidDataException("同一项目的required依赖要求不同版本。");
                return;
            }
            var project = await dependencies.GetContentProjectAsync(projectId, ct).ConfigureAwait(false);
            // Modrinth v2 represents dual mod/datapack projects as "mod"; require the selected release's datapack identity.
            if (worldDatapacks && project.Kind == "mod" && release.Loaders.Contains("datapack", StringComparer.Ordinal)) project = project with { Kind = "datapack" };
            if (project.Id != projectId || (depth == 0 && project.Kind != request.Kind)) throw new InvalidDataException("资源类型或项目身份已改变，请重新选择。");
            if (worldDatapacks && project.Kind != "datapack") throw new NotSupportedException("数据包的 required 依赖须为数据包；其它类型请先明确安装。");
            var folder = worldDatapacks ? "datapacks" : Folder(project.Kind);
            if (!Compatible(release, project.Kind, gameVersion, instance.Loader) || !worldDatapacks && (project.ClientSide == "unsupported" ||
                release.Environment is "server_only" or "server_only_client_optional" or "dedicated_server_only"))
                throw new InvalidDataException("所选资源或required依赖不适用于此客户端实例的游戏版本/加载器。");
            var selected = explicitFile is not null ? release.Files.SingleOrDefault(f => f.FileName == explicitFile)
                : release.Files.Count(f => f.Primary) == 1 ? release.Files.Single(f => f.Primary) : release.Files.Count == 1 ? release.Files[0] : null;
            if (selected is null) throw new InvalidDataException("资源文件不明确，请选择具体文件；依赖须提供唯一主文件。");
            ContentInstallFiles.ValidateFile(selected);
            if (project.Kind == "mod" && !selected.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                project.Kind is "resourcepack" or "shader" or "datapack" && !selected.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("此类型资源的文件扩展名不受支持。");
            total = checked(total + selected.Size); if (total > MaximumTotalBytes) throw new InvalidDataException("资源与required依赖总大小超过2 GiB限制。");
            active.Add(projectId);
            foreach (var dependency in release.Dependencies ?? [])
            {
                // WPF resource updates replace selected archives; updating a texture must not install or change mods.
                if (replacingResourcePath is not null)
                {
                    if (dependency.Type is "required" or "incompatible") warnings.Add("资源版本声明了依赖，请在资源详情核对；本次仅更新所选文件。");
                    continue;
                }
                if (dependency.Type == "incompatible") { incompatibilities.Add(dependency); continue; }
                if (dependency.Type is "optional" or "embedded") { if (dependency.Type == "optional") warnings.Add("可选依赖未自动安装：" + (dependency.ProjectId ?? dependency.VersionId ?? dependency.FileName ?? "未命名")); continue; }
                if (dependency.Type != "required") throw new InvalidDataException("未知依赖类型，无法保证完整安装。");
                ContentRelease child;
                if (dependency.VersionId is { } versionId) child = await dependencies.GetContentReleaseAsync(SafePath.Identifier(versionId), ct).ConfigureAwait(false);
                else if (dependency.ProjectId is { } childId)
                {
                    SafePath.Identifier(childId);
                    var childProject = await dependencies.GetContentProjectAsync(childId, ct).ConfigureAwait(false);
                    var dependencyLoader = worldDatapacks ? childId.StartsWith("cf-", StringComparison.Ordinal) ? null : "datapack" : childProject.Kind == "mod" ? instance.Loader : null;
                    var existing = (installedMods?.Files ?? []).Where(f => f.ProjectId == childId && !f.RelativePath.EndsWith(".disabled", StringComparison.Ordinal)).ToArray();
                    if (existing.Length == 1 && await ContentInstallFiles.MatchesAsync(ContentInstallFiles.Literal(instance.GameDirectory, existing[0].RelativePath), existing[0].Sha1, existing[0].Size, ct).ConfigureAwait(false))
                    {
                        child = await dependencies.GetContentReleaseAsync(existing[0].ReleaseId, ct).ConfigureAwait(false);
                        if (!child.Files.Any(f => f.Sha1 == existing[0].Sha1 && f.Size == existing[0].Size && existing[0].RelativePath == "mods/" + f.FileName)) throw new InvalidDataException("已装依赖的在线文件身份不符，请先核对。");
                    }
                    else
                    {
                        var choices = await catalog.GetContentReleasesAsync(childId, gameVersion, dependencyLoader, ct).ConfigureAwait(false);
                        child = choices.Where(r => Compatible(r, worldDatapacks ? "datapack" : childProject.Kind, gameVersion, instance.Loader))
                            .OrderByDescending(r => r.Published).FirstOrDefault() ?? throw new InvalidDataException("required依赖没有兼容此实例的版本。");
                    }
                }
                else throw new NotSupportedException("required外部文件依赖缺少可验证的项目/版本身份，需要手动处理。");
                await Visit(child, dependency.ProjectId, dependency.FileName, depth + 1).ConfigureAwait(false);
            }
            active.Remove(projectId); resolved.Add(projectId, release);
            // Game selections refer to installed filenames. Retain custom names and enabled pack/shader selections.
            files.Add(new(projectId, release.Id, project.Kind, depth == 0 && replacingResourcePath is not null ? replacingResourcePath : folder + "/" + selected.FileName, selected, depth > 0));
        }
        var root = await dependencies.GetContentReleaseAsync(request.ReleaseId, ct).ConfigureAwait(false);
        if (root.Id != request.ReleaseId) throw new InvalidDataException("资源版本身份不一致。");
        await Visit(root, request.ProjectId, request.FileName, 0).ConfigureAwait(false);
        if (files.Select(f => ContentInstallFiles.PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != files.Count) throw new InvalidDataException("依赖文件名称存在大小写或Unicode形式冲突。");
        foreach (var incompatible in incompatibilities)
            if (files.Any(f => Matches(f.ProjectId, f.ReleaseId, incompatible))) throw new InvalidDataException("依赖声明了与本次安装不兼容的资源。");
        var installed = installedMods ?? await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        foreach (var item in installed.Files)
        {
            if (incompatibilities.Any(d => Matches(item.ProjectId, item.ReleaseId, d)) ||
                (!updatingDatapacks || files.All(f => f.ProjectId != item.ProjectId)) && !(replacingModPaths?.Contains(item.RelativePath) ?? false)
                && item.RelativePath != replacingResourcePath && files.Any(f => f.ProjectId == item.ProjectId && f.ReleaseId != item.ReleaseId))
                throw new InvalidDataException("安装清单存在其他版本或不兼容资源，请先显式处理旧文件。");
        }
        IReadOnlyList<ContentInstallFile> checkedFiles = updatingDatapacks ? files.Where(f => !installed.Files.Any(old => old.RelativePath == f.RelativePath && files.Any(next => next.ProjectId == old.ProjectId))).ToArray()
            : replacingModPaths is not null ? files.Where(f => !replacingModPaths.Contains(f.RelativePath)).ToArray()
            : replacingResourcePath is not null ? files.Where(f => f.RelativePath != replacingResourcePath).ToArray() : files;
        await ContentInstallFiles.CheckConflictsAsync(instance.GameDirectory, checkedFiles, ct).ConfigureAwait(false);
        if (request.Kind == "shader") warnings.Add("文件装入shaderpacks；启用光影仍需实例具备相应渲染模组。");
        return new(instance.Id, gameVersion, instance.Loader, files, warnings.Distinct().ToArray());
    }
    private static bool Matches(string project, string version, ContentDependency dependency) =>
        dependency.VersionId is { } id ? id == version : dependency.ProjectId is { } p && p == project;
    public static string Folder(string kind) => kind switch { "mod" => "mods", "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => throw new NotSupportedException("首批实例资源安装支持模组、资源包与光影包；数据包须选择具体存档。") };
    public static bool Compatible(ContentRelease release, string kind, string gameVersion, string loader) =>
        release.GameVersions.Contains(gameVersion, StringComparer.Ordinal) &&
        (kind == "datapack" || release.Environment is not ("server_only" or "server_only_client_optional" or "dedicated_server_only")) &&
        (kind != "mod" || Instances.InstanceIdentity.SupportsModDirectory(loader) && release.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));
}
