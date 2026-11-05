using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Content;

public sealed class ContentInstallPlanner(ILauncherCatalogService catalog, IContentDependencyCatalogService dependencies)
{
    public const int MaximumProjects = 64, MaximumDepth = 16;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public async Task<ContentInstallPlan> PlanAsync(GameInstance instance, string gameVersion, ContentInstallRequest request, CancellationToken ct = default)
    {
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
            if (project.Id != projectId || (depth == 0 && project.Kind != request.Kind)) throw new InvalidDataException("资源类型或项目身份已改变，请重新选择。");
            var folder = Folder(project.Kind);
            if (!Compatible(release, project.Kind, gameVersion, instance.Loader) || project.ClientSide == "unsupported" ||
                release.Environment is "server_only" or "server_only_client_optional" or "dedicated_server_only")
                throw new InvalidDataException("所选资源或required依赖不适用于此客户端实例的游戏版本/加载器。");
            var selected = explicitFile is not null ? release.Files.SingleOrDefault(f => f.FileName == explicitFile)
                : release.Files.Count(f => f.Primary) == 1 ? release.Files.Single(f => f.Primary) : release.Files.Count == 1 ? release.Files[0] : null;
            if (selected is null) throw new InvalidDataException("资源文件不明确，请选择具体文件；依赖须提供唯一主文件。");
            ContentInstallFiles.ValidateFile(selected);
            if (project.Kind == "mod" && !selected.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                project.Kind is "resourcepack" or "shader" && !selected.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("此类型资源的文件扩展名不受支持。");
            total = checked(total + selected.Size); if (total > MaximumTotalBytes) throw new InvalidDataException("资源与required依赖总大小超过2 GiB限制。");
            active.Add(projectId);
            foreach (var dependency in release.Dependencies ?? [])
            {
                if (dependency.Type == "incompatible") { incompatibilities.Add(dependency); continue; }
                if (dependency.Type is "optional" or "embedded") { if (dependency.Type == "optional") warnings.Add("可选依赖未自动安装：" + (dependency.ProjectId ?? dependency.VersionId ?? dependency.FileName ?? "未命名")); continue; }
                if (dependency.Type != "required") throw new InvalidDataException("未知依赖类型，无法保证完整安装。");
                ContentRelease child;
                if (dependency.VersionId is { } versionId) child = await dependencies.GetContentReleaseAsync(SafePath.Identifier(versionId), ct).ConfigureAwait(false);
                else if (dependency.ProjectId is { } childId)
                {
                    SafePath.Identifier(childId);
                    var childProject = await dependencies.GetContentProjectAsync(childId, ct).ConfigureAwait(false);
                    var choices = await catalog.GetContentReleasesAsync(childId, gameVersion, childProject.Kind == "mod" ? instance.Loader : null, ct).ConfigureAwait(false);
                    child = choices.Where(r => Compatible(r, childProject.Kind, gameVersion, instance.Loader))
                        .OrderByDescending(r => r.Published).FirstOrDefault() ?? throw new InvalidDataException("required依赖没有兼容此实例的版本。");
                }
                else throw new NotSupportedException("required外部文件依赖缺少可验证的项目/版本身份，需要手动处理。");
                await Visit(child, dependency.ProjectId, dependency.FileName, depth + 1).ConfigureAwait(false);
            }
            active.Remove(projectId); resolved.Add(projectId, release);
            files.Add(new(projectId, release.Id, project.Kind, folder + "/" + selected.FileName, selected, depth > 0));
        }
        var root = await dependencies.GetContentReleaseAsync(request.ReleaseId, ct).ConfigureAwait(false);
        if (root.Id != request.ReleaseId) throw new InvalidDataException("资源版本身份不一致。");
        await Visit(root, request.ProjectId, request.FileName, 0).ConfigureAwait(false);
        if (files.Select(f => ContentInstallFiles.PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != files.Count) throw new InvalidDataException("依赖文件名称存在大小写或Unicode形式冲突。");
        foreach (var incompatible in incompatibilities)
            if (files.Any(f => Matches(f.ProjectId, f.ReleaseId, incompatible))) throw new InvalidDataException("依赖声明了与本次安装不兼容的资源。");
        var installed = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        foreach (var item in installed.Files)
        {
            if (files.Any(f => f.ProjectId == item.ProjectId && f.ReleaseId != item.ReleaseId) || incompatibilities.Any(d => Matches(item.ProjectId, item.ReleaseId, d)))
                throw new InvalidDataException("安装清单存在其他版本或不兼容资源，请先显式处理旧文件。");
        }
        await ContentInstallFiles.CheckConflictsAsync(instance.GameDirectory, files, ct).ConfigureAwait(false);
        if (request.Kind == "shader") warnings.Add("文件装入shaderpacks；启用光影仍需实例具备相应渲染模组。");
        return new(instance.Id, gameVersion, instance.Loader, files, warnings.Distinct().ToArray());
    }
    private static bool Matches(string project, string version, ContentDependency dependency) =>
        dependency.VersionId is { } id ? id == version : dependency.ProjectId is { } p && p == project;
    public static string Folder(string kind) => kind switch { "mod" => "mods", "resourcepack" => "resourcepacks", "shader" => "shaderpacks", _ => throw new NotSupportedException("首批实例资源安装支持模组、资源包与光影包；数据包须选择具体存档。") };
    public static bool Compatible(ContentRelease release, string kind, string gameVersion, string loader) =>
        release.GameVersions.Contains(gameVersion, StringComparer.Ordinal) &&
        release.Environment is not ("server_only" or "server_only_client_optional" or "dedicated_server_only") &&
        (kind != "mod" || loader != "vanilla" && release.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));
}
