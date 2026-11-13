using System.Collections.Concurrent;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Content;

public static class FavoriteDownloads
{
    private static IEnumerable<ContentRelease> Available(ContentProject project, IReadOnlyDictionary<string, IReadOnlyList<ContentRelease>> releases, string loader) => releases.GetValueOrDefault(project.Id, []).Where(r => r.Files.Count > 0 && (project.Kind != "mod" || loader == "any" || r.Loaders.Contains(loader)) && (r.ProjectId is null || r.ProjectId == project.Id)).OrderByDescending(r => r.Published);
    public static IReadOnlyList<string> CommonLoaders(IReadOnlyList<ContentProject> projects, IReadOnlyDictionary<string, IReadOnlyList<ContentRelease>> releases)
    {
        HashSet<string>? common = null;
        foreach (var project in projects.Where(p => p.Kind == "mod"))
        { var supported = Available(project, releases, "any").SelectMany(r => r.Loaders).ToHashSet(); if (common is null) common = supported; else common.IntersectWith(supported); }
        return common is null ? ["any"] : common.Order(StringComparer.Ordinal).ToArray();
    }
    public static IReadOnlyList<string> CommonVersions(IReadOnlyList<ContentProject> projects, IReadOnlyDictionary<string, IReadOnlyList<ContentRelease>> releases, string loader)
    {
        string[]? common = null;
        foreach (var project in projects) { var versions = Available(project, releases, loader).SelectMany(r => r.GameVersions).Distinct().ToArray(); common = common is null ? versions : common.Intersect(versions).ToArray(); }
        return common ?? [];
    }
    public static IReadOnlyList<FavoriteDownloadEntry> Plan(IReadOnlyList<ContentProject> projects, IReadOnlyDictionary<string, IReadOnlyList<ContentRelease>> releases, string loader, string version)
    {
        if (projects.Count == 0 || !CommonLoaders(projects, releases).Contains(loader) || !CommonVersions(projects, releases, loader).Contains(version)) throw new InvalidDataException("所选收藏没有共同支持的加载器或游戏版本。");
        List<FavoriteDownloadEntry> plan = []; var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var release = Available(project, releases, loader).First(r => r.GameVersions.Contains(version)); var file = release.Files.OrderByDescending(f => f.Primary).First(); ContentInstallFiles.ValidateFile(file);
            var name = file.FileName;
            if (names.TryGetValue(name, out var hash) && !hash.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name) + "-" + file.Sha1.ToLowerInvariant() + Path.GetExtension(name);
            names[name] = file.Sha1; plan.Add(new(project.Id, project.Title, file, name));
        }
        return plan;
    }
    public static async Task<FavoriteDownloadResult> ExportAsync(IReadOnlyList<FavoriteDownloadEntry> plan, string directory, Func<ContentFile, CancellationToken, Task<string>> download, CancellationToken ct = default, int concurrency = 1)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择已有的本机保存目录。");
        // Native folder pickers may return a trailing separator; it is not a link alias.
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(directory)) throw new ArgumentException("请选择已有的本机保存目录。"); SafePath.RejectLinks(directory);
        var failures = new ConcurrentBag<FavoriteDownloadFailure>(); var added = 0; var reused = 0;
        // Serialize aliases of the same destination; independent destinations can transfer concurrently.
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 8));
        await Task.WhenAll(plan.GroupBy(e => e.DestinationName, StringComparer.OrdinalIgnoreCase).Select(async entries =>
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested(); string? temporary = null;
                    try
                    {
                        ContentInstallFiles.ValidateFile(entry.File); var name = entry.DestinationName;
                        if (name.Length > 255 || name != Path.GetFileName(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") throw new InvalidDataException("保存文件名无效。");
                        var target = SafePath.Within(directory, name); SafePath.RejectLinks(target);
                        async Task<bool> Matches(string path) { await using var input = File.OpenRead(path); return input.Length == entry.File.Size && Convert.ToHexString(await SHA1.HashDataAsync(input, ct)).Equals(entry.File.Sha1, StringComparison.OrdinalIgnoreCase); }
                        if (File.Exists(target)) { if (!await Matches(target)) throw new IOException("目标文件已存在，未覆盖。"); Interlocked.Increment(ref reused); continue; }
                        var source = await download(entry.File, ct); SafePath.RejectLinks(source); temporary = SafePath.Within(directory, ".shou-download-" + Guid.NewGuid().ToString("N") + ".partial");
                        await using (var input = File.OpenRead(source)) await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) await input.CopyToAsync(output, ct);
                        if (!await Matches(temporary)) throw new InvalidDataException("下载文件大小或哈希校验失败。"); ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(target); File.Move(temporary, target, false); temporary = null; Interlocked.Increment(ref added);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException) { failures.Add(new(entry.Title, ex.Message)); }
                    finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            finally { gate.Release(); }
        }));
        return new(added, reused, failures.ToArray());
    }
}
