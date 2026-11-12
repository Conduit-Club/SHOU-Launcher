using System.Collections.Concurrent;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceModUpdateService, IInstanceModUpdateProgressService, IInstanceModRefreshService
{
    private sealed record ModUpdateState(GameInstance Instance, ContentInstalledFile Old, ContentRelease Release, ContentFile File, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, ModUpdateState> modUpdates = new();
    public async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string instanceId, CancellationToken cancellationToken = default)
        => await CheckInstanceModUpdatesAsync(instanceId, TimeSpan.FromMinutes(4), cancellationToken).ConfigureAwait(false);
    public Task<InstanceModUpdateCheck> CheckInstanceModUpdatesWithProgressAsync(string instanceId, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        => CheckInstanceModUpdatesAsync(instanceId, TimeSpan.FromMinutes(4), cancellationToken, new PackProgressReporter(progress));
    private sealed record ScanFile(InstanceContentEntry Row, long Size, string Sha1, string Sha512, ContentInstalledFile? Origin, string CacheKey, ModOnlineCache.Entry? Cached);
    public Task<InstanceModUpdateCheck> RefreshInstanceModUpdatesAsync(string instanceId, IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => CheckInstanceModUpdatesAsync(instanceId, TimeSpan.FromMinutes(4), cancellationToken, progress is null ? null : new PackProgressReporter(progress), true);
    internal async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string instanceId, TimeSpan budget, CancellationToken cancellationToken = default, IProgress<PackInstallProgress>? progress = null, bool refresh = false)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var instance = ContentTarget(instanceId, false);
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, cancellationToken).ConfigureAwait(false);
        var rows = InstanceWorkspace.List(instance, "mods", cancellationToken);
        var updates = new ConcurrentBag<InstanceModUpdate>(); var notices = new ConcurrentBag<string>(); var info = new ConcurrentBag<InstanceModOnlineInfo>();
        var files = new ConcurrentBag<ScanFile>(); var attempted = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(budget);
        using var transport = ContentTransport(); var curseForge = new CurseForgeCatalog(transport, build.CurseForgeApiKey);
        foreach (var item in modUpdates.Where(p => p.Value.Expires < DateTimeOffset.UtcNow || p.Value.Instance.Id == instanceId)) modUpdates.TryRemove(item.Key, out _);
        var checkedFiles = 0; progress?.Report(new("核对模组更新", 0, rows.Count));
        var configuration = Snapshot.Settings.DownloadSourcesJson + ":" + (Snapshot.Settings.Downloads?.SourcePreference.ToString() ?? "") + ":" + !string.IsNullOrWhiteSpace(build.CurseForgeApiKey);
        void Complete(InstanceContentEntry row) { if (attempted.TryAdd(row.RelativePath, 0)) progress?.Report(new("核对模组更新", Interlocked.Increment(ref checkedFiles), rows.Count, row.Name)); }
        try
        {
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (row, ct) =>
            {
                try
                {
                    var path = ContentInstallFiles.Literal(instance.GameDirectory, row.RelativePath);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    var size = stream.Length; var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct)); stream.Position = 0;
                    var sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct));
                    var tracked = manifest.Files.SingleOrDefault(f => EnabledModPath(f.RelativePath) == EnabledModPath(row.RelativePath));
                    if (tracked is not null && (tracked.Size != size || tracked.Sha1 != sha1 || tracked.Sha512 != sha512)) throw new IOException("文件已被修改，请先核对来源。");
                    var origin = tracked is null ? null : NormalizeModOrigin(tracked);
                    var key = sha1 + ":" + size + ":" + InstanceIdentity.GameVersion(instance) + ":" + instance.Loader + ":" + origin?.ProjectId + ":" + origin?.ReleaseId + ":" + configuration;
                    files.Add(new(row, size, sha1, sha512, origin, key, refresh ? null : await ModOnlineCache.ReadAsync(Paths.CacheDirectory, key, ct)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { notices.Add(row.Name + "：无法核实本地文件，未计为最新版。"); Complete(row); }
            });
            var unknown = files.Where(f => f.Cached is null && (f.Origin is null || !IsNumericCfProject(f.Origin.ProjectId))).ToArray();
            IReadOnlyDictionary<string, ContentRelease> modrinth = new Dictionary<string, ContentRelease>();
            var batchFailed = false;
            if (unknown.Length > 0)
            {
                try { using var batch = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); batch.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(2000, Math.Max(50, budget.TotalMilliseconds / 4)))); modrinth = await Catalog.ReleasesFromHashesAsync(unknown.Select(f => f.Sha1).ToArray(), batch.Token); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { batchFailed = true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or AggregateException or InvalidOperationException) { batchFailed = true; }
            }
            var cfMatches = new Dictionary<string, ContentRelease>(StringComparer.Ordinal);
            if (!batchFailed && !string.IsNullOrWhiteSpace(build.CurseForgeApiKey))
            {
                var hashes = new List<(ScanFile File, uint Fingerprint)>();
                foreach (var file in unknown.Where(f => !modrinth.ContainsKey(f.Sha1)))
                {
                    try { await using var stream = File.OpenRead(ContentInstallFiles.Literal(instance.GameDirectory, file.Row.RelativePath)); hashes.Add((file, await CurseForgeFingerprint.ComputeAsync(stream, timeout.Token))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                try
                {
                    var matches = await curseForge.MatchFingerprintsAsync(hashes.Select(f => f.Fingerprint).ToArray(), timeout.Token);
                    foreach (var pair in hashes) if (matches.TryGetValue(pair.Fingerprint, out var release) && release.Files.Any(f => f.Sha1 == pair.File.Sha1 && f.Size == pair.File.Size)) cfMatches[pair.File.Sha1] = release;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException) { }
            }
            var releaseQueries = new ConcurrentDictionary<string, Task<IReadOnlyList<ContentRelease>>>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (file, ct) =>
            {
                var row = file.Row; var verified = false;
                try
                {
                    var origin = file.Origin; var fromCurseForge = origin is not null && IsNumericCfProject(origin.ProjectId);
                    if (fromCurseForge && !origin!.ReleaseId.StartsWith(origin.ProjectId + "-", StringComparison.Ordinal)) throw new InvalidDataException("CurseForge 版本身份不完整。");
                    var current = file.Cached?.Current ?? (fromCurseForge ? await curseForge.ReleaseAsync(origin!.ReleaseId, ct)
                        : modrinth.TryGetValue(file.Sha1, out var mr) ? mr : cfMatches.TryGetValue(file.Sha1, out var cf) ? cf : await Catalog.ReleaseFromSha1Async(file.Sha1, ct));
                    if (fromCurseForge && current.ProjectId != origin!.ProjectId) throw new InvalidDataException("在线项目与安装清单不符。");
                    if (current.ProjectId is not { } project || !current.Files.Any(f => f.Sha1 == file.Sha1 && f.Size == file.Size)) throw new InvalidDataException("本地文件与在线身份不符。");
                    fromCurseForge = IsNumericCfProject(project);
                    ContentRelease? next; ContentProject? projectInfo = file.Cached?.Project;
                    if (file.Cached is { } cached) next = cached.Next;
                    else
                    {
                        var releases = await releaseQueries.GetOrAdd(project, _ => fromCurseForge ? curseForge.ReleasesAsync(project, InstanceIdentity.GameVersion(instance), instance.Loader, ct) : Catalog.ReleasesAsync(project, InstanceIdentity.GameVersion(instance), instance.Loader, ct));
                        next = releases.Where(r => r.ProjectId == project && r.ReleaseChannel == "release" && r.Published > current.Published && ContentInstallPlanner.Compatible(r, "mod", InstanceIdentity.GameVersion(instance), instance.Loader))
                            .OrderByDescending(r => r.Published).FirstOrDefault(r => r.Files.Count(f => f.Primary) == 1 || r.Files.Count == 1);
                    }
                    verified = true;
                    var checkedAt = file.Cached?.Checked ?? DateTimeOffset.UtcNow;
                    try { await ModOnlineCache.WriteAsync(Paths.CacheDirectory, file.CacheKey, new(1, checkedAt, current, next, projectInfo), ct); }
                    catch (IOException) { }

                    if (next is not null && next.ProjectId == project && next.ReleaseChannel == "release" && next.Published > current.Published && ContentInstallPlanner.Compatible(next, "mod", InstanceIdentity.GameVersion(instance), instance.Loader))
                    {
                        var target = next.Files.Count == 1 ? next.Files[0] : next.Files.Single(f => f.Primary);
                        if (target.Sha1 != file.Sha1)
                        {
                            ContentInstallFiles.ValidateFile(target);
                            var token = Guid.NewGuid().ToString("N"); var old = new ContentInstalledFile(project, current.Id, "mod", row.RelativePath, file.Sha1, file.Sha512, file.Size, Client: origin?.Client ?? "required", Server: origin?.Server ?? "unsupported");
                            modUpdates[token] = new(instance, old, next, target, DateTimeOffset.UtcNow.AddMinutes(15));
                            var page = fromCurseForge ? new Uri("https://www.curseforge.com/projects/" + project[3..] + "/files") : new Uri("https://modrinth.com/mod/" + project + "/changelog");
                            updates.Add(new(token, instance.Id, row.RelativePath, row.DisplayName, row.ModVersion ?? current.Version, next.Version, target.Size, page, project));
                        }
                    }
                    if (projectInfo is null)
                    {
                        try
                        {
                            using var metadata = CancellationTokenSource.CreateLinkedTokenSource(ct); metadata.CancelAfter(TimeSpan.FromSeconds(2));
                            projectInfo = fromCurseForge ? await curseForge.ProjectAsync(project, metadata.Token) : await Catalog.ProjectAsync(project, metadata.Token);
                            await ModOnlineCache.WriteAsync(Paths.CacheDirectory, file.CacheKey, new(1, checkedAt, current, next, projectInfo), ct);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or AggregateException or InvalidOperationException) { }
                    }
                    if (projectInfo is not null && projectInfo.Id == project) info.Add(new(row.RelativePath, projectInfo));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { if (!verified) notices.Add(row.Name + "：在线检查超时，尚未核实更新；未改动此文件。"); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or AggregateException or InvalidOperationException or System.Text.Json.JsonException) { notices.Add(row.Name + "：暂未能核实官方更新，未计为最新版；未改动此文件。"); }
                finally { Complete(row); }
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { }
        foreach (var row in rows.Where(r => !attempted.ContainsKey(r.RelativePath))) notices.Add(row.Name + "：本次检查达到时间上限，尚未查询；可稍后重试，未改动此文件。");
        cancellationToken.ThrowIfCancellationRequested(); progress?.Report(new("检查结束", checkedFiles, rows.Count));
        return new(updates.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray(), checkedFiles, notices.Order(StringComparer.Ordinal).ToArray(), info.ToArray());
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
            var paths = new HashSet<string>(StringComparer.Ordinal) { state.Old.RelativePath, EnabledModPath(state.Old.RelativePath) };
            var request = new ContentInstallRequest(instance.Id, state.Old.ProjectId, state.Release.Id, state.File.FileName, "mod");
            var replacing = new HashSet<string>(StringComparer.Ordinal) { EnabledModPath(state.Old.RelativePath) };
            normalized = normalized with { Files = normalized.Files.Where(f => !paths.Contains(f.RelativePath)).ToArray() };
            var plan = await new ContentInstallPlanner(this, this).PlanAsync(instance, InstanceIdentity.GameVersion(instance), request, ct, replacingModPaths: replacing, installedMods: normalized).ConfigureAwait(false);
            await RunContentTransferAsync("mod-update:" + instance.Id, retry => ApplyInstanceModUpdateAsync(token, retry), async transfer =>
                result = await ModUpdateFiles.ApplyAsync(instance, state.Old, plan, DownloadInstallFileAsync, () => EnsureInstanceNotRunning(instance), transfer).ConfigureAwait(false), ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        modUpdates.TryRemove(token, out _); return result!;
    }
    private static string EnabledModPath(string path) => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
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
