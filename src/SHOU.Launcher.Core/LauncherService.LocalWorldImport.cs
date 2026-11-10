using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILocalWorldImportService
{
    private sealed record LocalWorldSource(string Path, WorldPathSnapshot Guard);
    public async Task<LocalWorldImportPreview> PreviewLocalWorldImportAsync(LocalWorldImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); WorldLeaf(request.WorldDirectoryName);
        if (!Path.IsPathFullyQualified(request.ArchivePath) || !Path.GetExtension(request.ArchivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择单个本地 Java 世界 ZIP 文件。");
        var path = Path.GetFullPath(request.ArchivePath);
        LocalWorldImportPreview? result = null;
        await WorldContentOperationAsync(request.InstanceId, token => PreviewLocalWorldImportAsync(request, token), async ct =>
        {
            var instance = ContentTarget(request.InstanceId, true); RequireMissingWorld(instance, request.WorldDirectoryName);
            var source = new LocalWorldSource(path, WorldPathSnapshot.Capture(Path.GetDirectoryName(path)!, path));
            var game = await WorldGameAsync(instance, ct); source.Guard.Verify();
            var world = await WorldContentArchive.WorldAsync(path, ct, allowFinderMetadata: true);
            if (world.Version != game.Version || world.DataVersion != game.DataVersion) throw new InvalidDataException("ZIP 内世界与所选实例版本不一致，不会自动升级或降级。");
            string sha;
            using (var zip = PackArchiveReader.Open(path)) sha = await zip.Sha256Async(ct);
            var inspect = ContentInstallFiles.Literal(Paths.TempDirectory, "world-local-inspect-" + Guid.NewGuid().ToString("N"));
            WorldPathSnapshot? ownedInspect = null;
            try
            {
                Directory.CreateDirectory(inspect); ownedInspect = WorldPathSnapshot.Capture(Paths.TempDirectory, inspect);
                var metadata = world.Files.Where(f => f == "level.dat" || f.StartsWith("datapacks/", StringComparison.Ordinal)).ToArray();
                await WorldContentArchive.ExtractWorldAsync(path, inspect, world with { Files = metadata }, sha, ct);
                await WorldContentArchive.ValidateBundledWorldPacksAsync(inspect, game.PackFormat, ct); ownedInspect.Verify();
            }
            finally
            {
                VerifiedDownloader.RejectLink(inspect);
                if (Directory.Exists(inspect) && ownedInspect is not null) { ownedInspect.Verify(); Directory.Delete(inspect, true); }
            }
            source.Guard.Verify(); ct.ThrowIfCancellationRequested(); EnsureInstanceNotRunning(instance); RequireMissingWorld(instance, request.WorldDirectoryName);
            var target = WorldPathSnapshot.Capture(instance.GameDirectory, ContentInstallFiles.Literal(instance.GameDirectory, "saves"), true);
            var preview = new WorldContentPreview(Guid.NewGuid().ToString("N"), "world", instance.Name, request.WorldDirectoryName, game.Version, world.Files.ToArray(),
                ["仅导入为新的存档文件夹，不覆盖已有存档。"]);
            var plan = new WorldDownloadPlan(preview, new(instance.Id, "", "", Path.GetFileName(path), "world", request.WorldDirectoryName), instance, game, null, world,
                new Dictionary<string, string> { ["world"] = path }, new Dictionary<string, string>(), sha, target, new HashSet<string>(), DateTimeOffset.UtcNow, source);
            worldDownloads[preview.Id] = plan;
            foreach (var key in worldDownloads.Keys.Where(k => k != preview.Id).Take(Math.Max(0, worldDownloads.Count - 8))) worldDownloads.TryRemove(key, out _);
            result = new(preview, world.Name ?? Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), sha);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public Task<WorldContentResult> ImportLocalWorldAsync(string previewId, CancellationToken cancellationToken = default)
    {
        if (!worldDownloads.TryGetValue(previewId, out var plan) || plan.LocalSource is null) throw new InvalidOperationException("本地存档预检已失效，请重新检查。");
        return InstallWorldContentAsync(previewId, cancellationToken);
    }
    public Task DiscardLocalWorldImportAsync(string previewId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (worldDownloads.TryGetValue(previewId, out var plan) && plan.LocalSource is not null) worldDownloads.TryRemove(previewId, out _);
        return Task.CompletedTask;
    }
    private static async Task VerifyLocalWorldSourceAsync(WorldDownloadPlan plan, CancellationToken ct)
    {
        if (plan.LocalSource is not { } source) return;
        source.Guard.Verify();
        using var zip = PackArchiveReader.Open(source.Path);
        if (await zip.Sha256Async(ct) != plan.Revision) throw new IOException("本地世界 ZIP 自预检后已改变，请重新检查。");
        source.Guard.Verify();
    }
}
