using System.Collections.Concurrent;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceModUpdateService, IInstanceModUpdateProgressService
{
    private sealed record ModUpdateState(GameInstance Instance, ContentInstalledFile Old, ContentRelease Release, ContentFile File, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, ModUpdateState> modUpdates = new();
    public async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string instanceId, CancellationToken cancellationToken = default)
        => await CheckInstanceModUpdatesAsync(instanceId, TimeSpan.FromMinutes(4), cancellationToken).ConfigureAwait(false);
    public Task<InstanceModUpdateCheck> CheckInstanceModUpdatesWithProgressAsync(string instanceId, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        => CheckInstanceModUpdatesAsync(instanceId, TimeSpan.FromMinutes(4), cancellationToken, new PackProgressReporter(progress));
    internal async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string instanceId, TimeSpan budget, CancellationToken cancellationToken = default, IProgress<PackInstallProgress>? progress = null)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var instance = ContentTarget(instanceId, false);
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, cancellationToken).ConfigureAwait(false);
        var rows = InstanceWorkspace.List(instance, "mods");
        var updates = new ConcurrentBag<InstanceModUpdate>(); var notices = new ConcurrentBag<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(budget);
        // A pack can contain hundreds of CF projects. Reuse connections across this scan.
        using var transport = ContentTransport(); var curseForge = new CurseForgeCatalog(transport, build.CurseForgeApiKey);
        var attempted = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        foreach (var item in modUpdates.Where(p => p.Value.Expires < DateTimeOffset.UtcNow || p.Value.Instance.Id == instanceId)) modUpdates.TryRemove(item.Key, out _);
        var checkedFiles = 0; progress?.Report(new("核对模组更新", 0, rows.Count));
        try
        {
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (row, ct) =>
            {
                try
                {
                    var path = ContentInstallFiles.Literal(instance.GameDirectory, row.RelativePath);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    var size = stream.Length; var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)); stream.Position = 0;
                    var sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false));
                    var tracked = manifest.Files.SingleOrDefault(f => EnabledModPath(f.RelativePath) == EnabledModPath(row.RelativePath));
                    if (tracked is not null && (tracked.Size != size || tracked.Sha1 != sha1 || tracked.Sha512 != sha512)) throw new IOException("文件已被修改，请先核对来源。");
                    var origin = tracked is null ? null : NormalizeModOrigin(tracked);
                    var fromCurseForge = origin is not null && IsNumericCfProject(origin.ProjectId);
                    if (fromCurseForge && !origin!.ReleaseId.StartsWith(origin.ProjectId + "-", StringComparison.Ordinal)) throw new InvalidDataException("安装清单中的 CurseForge 版本身份不完整。");
                    var current = fromCurseForge
                        ? await curseForge.ReleaseAsync(origin!.ReleaseId, ct).ConfigureAwait(false)
                        : await Catalog.ReleaseFromSha1Async(sha1, ct).ConfigureAwait(false);
                    if (fromCurseForge && current.ProjectId != origin!.ProjectId) throw new InvalidDataException("在线模组项目与安装清单不符。");
                    if (current.ProjectId is not { } project || !current.Files.Any(f => f.Sha1 == sha1 && f.Size == size)) throw new InvalidDataException("本地文件与在线版本身份不符。");
                    var releases = fromCurseForge
                        ? await curseForge.ReleasesAsync(project, InstanceIdentity.GameVersion(instance), instance.Loader, ct).ConfigureAwait(false)
                        : await Catalog.ReleasesAsync(project, InstanceIdentity.GameVersion(instance), instance.Loader, ct).ConfigureAwait(false);
                    var next = releases.Where(r => r.ProjectId == project && r.ReleaseChannel == "release" && r.Published > current.Published && ContentInstallPlanner.Compatible(r, "mod", InstanceIdentity.GameVersion(instance), instance.Loader))
                        .OrderByDescending(r => r.Published).FirstOrDefault(r => r.Files.Count(f => f.Primary) == 1 || r.Files.Count == 1);
                    if (next is null) return;
                    var file = next.Files.Count == 1 ? next.Files[0] : next.Files.Single(f => f.Primary);
                    if (file.Sha1 == sha1) return;
                    ContentInstallFiles.ValidateFile(file);
                    var token = Guid.NewGuid().ToString("N");
                    var old = new ContentInstalledFile(project, current.Id, "mod", row.RelativePath, sha1, sha512, size);
                    modUpdates[token] = new(instance, old, next, file, DateTimeOffset.UtcNow.AddMinutes(15));
                    updates.Add(new(token, instance.Id, row.RelativePath, row.DisplayName, row.ModVersion ?? current.Version, next.Version, file.Size));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                { notices.Add(row.Name + "：在线检查超时，尚未核实更新；未改动此文件。"); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or AggregateException or InvalidOperationException)
                { notices.Add(row.Name + "：暂未能核实官方更新，未改动此文件；请核对本地来源或稍后重试。"); }
                finally { attempted.TryAdd(row.RelativePath, 0); var count = Interlocked.Increment(ref checkedFiles); progress?.Report(new("核对模组更新", count, rows.Count, row.Name)); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            foreach (var row in rows.Where(r => !attempted.ContainsKey(r.RelativePath)))
                notices.Add(row.Name + "：本次检查达到时间上限，尚未查询；可稍后重试，未改动此文件。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("检查结束", checkedFiles, rows.Count));
        return new(updates.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray(), checkedFiles, notices.Order(StringComparer.Ordinal).ToArray());
    }
    public async Task<InstanceModUpdateResult> ApplyInstanceModUpdateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!modUpdates.TryGetValue(token, out var state) || state.Expires < DateTimeOffset.UtcNow) throw new InvalidOperationException("更新检查已过期，请重新检查。");
        InstanceModUpdateResult? result = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var instance = ContentTarget(state.Instance.Id, true);
            if (instance != state.Instance) throw new IOException("实例配置已改变，请重新检查更新。");
            var previous = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
            var normalized = previous with { Files = previous.Files.Select(NormalizeModOrigin).ToArray() };
            var paths = new HashSet<string>(StringComparer.Ordinal) { state.Old.RelativePath, state.Old.RelativePath.EndsWith(".disabled", StringComparison.Ordinal) ? state.Old.RelativePath[..^9] : state.Old.RelativePath };
            var request = new ContentInstallRequest(instance.Id, state.Old.ProjectId, state.Release.Id, state.File.FileName, "mod");
            var replacing = new HashSet<string>(StringComparer.Ordinal) { state.Old.RelativePath.EndsWith(".disabled", StringComparison.Ordinal) ? state.Old.RelativePath[..^9] : state.Old.RelativePath };
            normalized = normalized with { Files = normalized.Files.Where(f => !paths.Contains(f.RelativePath)).ToArray() };
            var plan = await new ContentInstallPlanner(this, this).PlanAsync(instance, InstanceIdentity.GameVersion(instance), request, ct, replacingModPaths: replacing, installedMods: normalized).ConfigureAwait(false);
            await RunContentTransferAsync("mod-update:" + instance.Id, retry => ApplyInstanceModUpdateAsync(token, retry), async transfer =>
                result = await ModUpdateFiles.ApplyAsync(instance, state.Old, plan, DownloadInstallFileAsync, () => EnsureInstanceNotRunning(instance), transfer).ConfigureAwait(false), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        modUpdates.TryRemove(token, out _); return result!;
    }
    private static string EnabledModPath(string path) => path.EndsWith(".disabled", StringComparison.Ordinal) ? path[..^9] : path;
    private static bool IsNumericCfProject(string value) => value.StartsWith("cf-", StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var project) && project > 0;
    internal static ContentInstalledFile NormalizeModOrigin(ContentInstalledFile file)
    {
        // Pack manifests historically store cf-<file>; catalog APIs use cf-<project>-<file>.
        // Keep existing manifests intact and join only independently recorded numeric identities.
        if (IsNumericCfProject(file.ProjectId))
        {
            var raw = file.ReleaseId.StartsWith("cf-", StringComparison.Ordinal) ? file.ReleaseId[3..] : file.ReleaseId;
            if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0)
                return file with { ReleaseId = file.ProjectId + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            return file;
        }
        if (!file.RelativePath.StartsWith("mods/", StringComparison.Ordinal) || file.DownloadUrl is null || !Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var uri) || !ContentInstallFiles.AllowedUri(uri)) return file;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts[0] == "data" && parts[2] == "versions" ? file with { ProjectId = parts[1], ReleaseId = parts[3] } : file;
    }
}
