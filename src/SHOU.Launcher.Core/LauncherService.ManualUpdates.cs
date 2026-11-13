using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    public async Task<IReadOnlyList<CurseForgeManualFile>> GetInstanceUpdateManualFilesAsync(string token, CancellationToken cancellationToken = default)
    {
        var state = RequireManualUpdate(token);
        var instance = ContentTarget(state.Instance.Id, false);
        if (instance != state.Instance) throw new IOException("实例配置已改变，请重新检查更新。");
        if (!await ContentInstallFiles.MatchesAsync(ContentInstallFiles.Literal(instance.GameDirectory, state.Old.RelativePath), state.Old.Sha1, state.Old.Size, cancellationToken))
            throw new IOException("原文件已改变，请重新检查更新。");
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, cancellationToken);
        var oldPaths = new HashSet<string>(StringComparer.Ordinal) { state.Old.RelativePath, EnabledModPath(state.Old.RelativePath) };
        manifest = manifest with { Files = manifest.Files.Select(NormalizeModOrigin).Where(f => !oldPaths.Contains(f.RelativePath)).ToArray() };
        var resource = state.Old.Kind is "resourcepack" or "shader";
        var plan = await new ContentInstallPlanner(this, this).PlanAsync(instance, Instances.InstanceIdentity.GameVersion(instance),
            new(instance.Id, state.Old.ProjectId, state.Release.Id, state.File.FileName, state.Old.Kind), cancellationToken,
            replacingModPaths: resource ? null : new HashSet<string>(StringComparer.Ordinal) { EnabledModPath(state.Old.RelativePath) },
            installedMods: manifest, replacingResourcePath: resource ? state.Old.RelativePath : null);
        if (!plan.Files.Any(f => !f.Dependency && f.File == state.File)) throw new IOException("在线文件已改变，请重新检查更新。");
        var missing = new List<CurseForgeManualFile>();
        foreach (var file in plan.Files.Select(f => f.File.ManualFile).OfType<CurseForgeManualFile>().Distinct())
            if (await CurseForgeManualCache.DigestAsync(Paths.CacheDirectory, file, cancellationToken) is null) missing.Add(file);
        return missing;
    }
    public async Task ProvideInstanceUpdateManualFileAsync(string token, CurseForgeManualFile file, string path, CancellationToken cancellationToken = default)
    {
        var state = RequireManualUpdate(token);
        var missing = await GetInstanceUpdateManualFilesAsync(token, cancellationToken);
        if (!missing.Contains(file)) throw new InvalidOperationException("此文件不属于当前更新，请重新检查。");
        await CurseForgeManualCache.StoreAsync(Paths.CacheDirectory, file, path, cancellationToken);
        if (!modUpdates.TryGetValue(token, out var current) || current != state) throw new InvalidOperationException("更新检查已改变，请重新检查。");
    }
    private ModUpdateState RequireManualUpdate(string token) => modUpdates.TryGetValue(token, out var state) && state.Expires >= DateTimeOffset.UtcNow
        ? state : throw new InvalidOperationException("更新检查已失效，请重新检查。");
}
