using System.Collections.Concurrent;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstanceContentMetadataService
{
    public async Task<InstanceContentInfoCheck> ReadInstanceContentMetadataAsync(string instanceId, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not ("resourcepacks" or "shaders")) throw new ArgumentException("此类别不使用资源展示信息查询。");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var instance = ContentTarget(instanceId, false); var projectKind = kind == "shaders" ? "shader" : "resourcepack";
        var rows = await Task.Run(() => InstanceWorkspace.List(instance, kind, cancellationToken), cancellationToken).ConfigureAwait(false);
        var info = new ConcurrentBag<InstanceContentOnlineInfo>(); var notices = new ConcurrentBag<string>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); lifetime.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = lifetime.Token }, async (row, token) =>
            {
                try
                {
                    using var item = CancellationTokenSource.CreateLinkedTokenSource(token); item.CancelAfter(TimeSpan.FromSeconds(8)); var ct = item.Token;
                    var path = ContentInstallFiles.Literal(instance.GameDirectory, row.RelativePath); var file = new FileInfo(path); var size = file.Length; var modified = file.LastWriteTimeUtc; string sha1;
                    await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true)) sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct));
                    var key = "resource-info-v1:" + kind + ":" + sha1 + ":" + size;
                    var cached = await ModOnlineCache.ReadAsync(Paths.CacheDirectory, key, ct);
                    var current = cached?.Current ?? await Catalog.ReleaseFromSha1Async(sha1, ct);
                    if (current.ProjectId is not { } id || !current.Files.Any(f => f.Sha1 == sha1 && f.Size == size)) throw new InvalidDataException("文件身份不匹配。");
                    var project = cached?.Project ?? await Catalog.ProjectAsync(id, ct);
                    if (project.Id != id || project.Kind != projectKind) throw new InvalidDataException("项目身份或类别不匹配。");
                    file.Refresh(); if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != modified) throw new IOException("资源在读取期间发生变化。");
                    info.Add(new(row.RelativePath, project, current.Version));
                    try { await ModOnlineCache.WriteAsync(Paths.CacheDirectory, key, new(1, cached?.Checked ?? DateTimeOffset.UtcNow, current, null, project), ct); }
                    catch (IOException) { }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { notices.Add(row.Name); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or AggregateException) { notices.Add(row.Name); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { foreach (var row in rows.Where(r => !info.Any(i => i.RelativePath == r.RelativePath))) notices.Add(row.Name); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(info.OrderBy(i => i.RelativePath, StringComparer.Ordinal).ToArray(), notices.Distinct(StringComparer.Ordinal).ToArray());
    }
}
