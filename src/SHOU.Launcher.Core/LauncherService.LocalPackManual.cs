using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    public async Task ProvideLocalPackFileAsync(string archivePath, string expectedArchiveSha256, int projectId, int fileId, string sourcePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!ContentInstallFiles.Hash(expectedArchiveSha256, 64)) throw new InvalidDataException("请先读取整合包。");
        var archive = await new LocalPackArchive().ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (!archive.CanResolveCurseForge || archive.Inspection.ArchiveSha256 != expectedArchiveSha256 || !archive.CurseForgeFiles.Any(f => f.Required && f.ProjectId == projectId && f.FileId == fileId)) throw new InvalidDataException("所补文件不属于已确认归档的必需清单。");
        using var transport = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        var resolution = await new CurseForgePackResolver(transport, build.CurseForgeApiKey, Paths.CacheDirectory, network)
            .ResolveAsync([new(projectId, fileId, true)], archive.Inspection.GameVersion, archive.Inspection.Loader, cancellationToken).ConfigureAwait(false);
        var file = resolution.ManualFiles?.SingleOrDefault(f => f.ProjectId == projectId && f.FileId == fileId) ?? throw new InvalidOperationException("此文件无需手动补充，或官方元数据尚未核实，请重新检查。");
        await CurseForgeManualCache.StoreAsync(Paths.CacheDirectory, file, sourcePath, cancellationToken).ConfigureAwait(false);
        localPackPlans.TryRemove(expectedArchiveSha256, out _);
    }
}
