using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tools;

/// <summary>Explicit, reproducible cache only. Never traverses links or deletes exports, runtimes, credentials or instances.</summary>
public static class LauncherCacheCleaner
{
    private static readonly HashSet<string> CacheFolders = new(StringComparer.Ordinal)
    { "assets", "authlib", "catalog", "club-updates", "content", "libraries", "loaders", "versions", "java-metadata" };
    private const int MaximumFiles = 100000;
    private static bool Allowed(string area, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.Contains('\\') || p.Contains(':'))) return false;
        if (area == "temporary") return parts[0].StartsWith("game-", StringComparison.Ordinal) || parts[0].StartsWith("loader-", StringComparison.Ordinal) || parts[0].StartsWith("installer-", StringComparison.Ordinal);
        return area == "cache" && (CacheFolders.Contains(parts[0]) || parts.Length == 1 && (relative.EndsWith(".json", StringComparison.Ordinal) || relative.EndsWith(".json.sha256", StringComparison.Ordinal)))
            && !relative.EndsWith(".lock", StringComparison.Ordinal);
    }
    private static string Resolve(IAppPaths paths, CacheFile file)
    {
        if (!Allowed(file.Area, file.RelativePath) || file.Bytes < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("缓存清理预览无效，请重新扫描。");
        var root = file.Area == "cache" ? paths.CacheDirectory : paths.TempDirectory;
        var full = Path.GetFullPath(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("缓存路径超出范围。");
        VerifiedDownloader.RejectLink(full); return full;
    }
    private static async Task<string> HashAsync(string file, CancellationToken ct)
    { await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); }
    private static bool Busy(string path)
    {
        foreach (var suffix in new[] { ".part.resume.json", ".aria2-part.aria2", ".aria2-part", ".part" })
            if (path.EndsWith(suffix, StringComparison.Ordinal)) { path = path[..^suffix.Length]; break; }
        foreach (var suffix in new[] { ".download.lock", ".coordinator.lock" })
        {
            var lease = path + suffix; VerifiedDownloader.RejectLink(lease);
            if (!File.Exists(lease)) continue;
            try { using var check = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return true; }
        }
        return false;
    }
    public static async Task<CacheCleanupPlan> ScanAsync(IAppPaths paths, CancellationToken ct = default)
    {
        List<CacheFile> files = []; var skipped = 0; var visited = 0;
        foreach (var (area, root) in new[] { ("cache", paths.CacheDirectory), ("temporary", paths.TempDirectory) })
        {
            VerifiedDownloader.RejectLink(root); if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(directory);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (++visited > MaximumFiles) throw new IOException("缓存超过 100000 项，请分批整理；未修改文件。");
                    if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                    if (entry is DirectoryInfo)
                    {
                        if (Allowed(area, relative + "/placeholder")) pending.Push(entry.FullName);
                        else skipped++;
                        continue;
                    }
                    var staging = relative.EndsWith(".part", StringComparison.Ordinal) || relative.EndsWith(".part.resume.json", StringComparison.Ordinal) || relative.EndsWith(".aria2", StringComparison.Ordinal);
                    if (!Allowed(area, relative) || (area == "temporary" || staging) && entry.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1) || Busy(entry.FullName)) { skipped++; continue; }
                    try { var file = (FileInfo)entry; files.Add(new(area, relative, file.Length, await HashAsync(file.FullName, ct))); }
                    catch (IOException) { skipped++; }
                }
            }
        }
        return new(files, skipped);
    }
    public static async Task<CacheCleanupResult> ClearAsync(IAppPaths paths, CacheCleanupPlan plan, CancellationToken ct = default)
    {
        if (plan.Files.Count > MaximumFiles || plan.Files.Select(f => f.Area + "/" + f.RelativePath.ToUpperInvariant()).Distinct().Count() != plan.Files.Count) throw new InvalidDataException("缓存清理预览无效。");
        // Check the entire preview before deleting anything; changed or replaced files require a new preview.
        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested(); var path = Resolve(paths, file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || Busy(path) || await HashAsync(path, ct) != file.Sha256) throw new IOException("缓存已变化或正在使用，请重新扫描；尚未清理文件。");
        }
        var count = 0; long bytes = 0; var skipped = plan.SkippedFiles;
        foreach (var file in plan.Files)
        {
            if (ct.IsCancellationRequested) return new(count, bytes, skipped, true);
            try
            {
                var path = Resolve(paths, file);
                // The exclusive handle prevents a writer from changing this file during the final check.
                await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete, 131072, true))
                {
                    if (Busy(path) || stream.Length != file.Bytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != file.Sha256) { skipped++; continue; }
                    File.Delete(path);
                }
                count++; bytes += file.Bytes;
            }
            catch (OperationCanceledException) { return new(count, bytes, skipped, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(count, bytes, skipped, false);
    }
}
