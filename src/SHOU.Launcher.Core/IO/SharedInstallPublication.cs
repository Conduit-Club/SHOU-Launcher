using System.Security.Cryptography;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.IO;

/// <summary>Publishes runtime files into a registered shared directory without replacing user files.</summary>
internal sealed class SharedInstallPublication(string destination) : IAsyncDisposable
{
    private readonly List<(string Path, string Hash)> added = [];
    private readonly List<string> directories = [];
    private byte[]? oldMarker;
    private string? markerHash;
    private bool complete;
    internal void Complete() => complete = true;
    private void EnsureDirectory(string path)
    {
        VerifiedDownloader.RejectLink(path);
        if (Directory.Exists(path)) return;
        EnsureDirectory(Path.GetDirectoryName(path)!); Directory.CreateDirectory(path); directories.Add(path);
    }
    internal async Task PublishAsync(string stage, CancellationToken ct)
    {
        if (SafePath.IsWithin(SafePath.Canonical(stage), SafePath.Canonical(destination))) throw new IOException("共享目录不能位于安装暂存内。");
        EnsureDirectory(destination);
        var paths = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(stage, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > 20000) throw new IOException("共享安装文件超过数量限制。");
        foreach (var relative in paths.Where(p => p != ".shou/install.json").Concat(paths.Contains(".shou/install.json") ? [".shou/install.json"] : []))
        {
            ct.ThrowIfCancellationRequested();
            if (!relative.StartsWith(".shou/", StringComparison.Ordinal) && !relative.StartsWith("resources/", StringComparison.Ordinal) && !relative.StartsWith("labymod-neo/", StringComparison.Ordinal)) throw new IOException("共享安装不发布用户内容文件。");
            var source = ContentInstallFiles.Literal(stage, relative); var target = ContentInstallFiles.Literal(destination, relative);
            var hash = await HashAsync(source, ct).ConfigureAwait(false);
            if (File.Exists(target) && relative != ".shou/install.json")
            { if (await HashAsync(target, ct) != hash) throw new IOException("共享目录已有不同内容，未覆盖：" + relative); continue; }
            EnsureDirectory(Path.GetDirectoryName(target)!);
            if (relative == ".shou/install.json" && File.Exists(target))
            {
                if (new FileInfo(target).Length > 64 * 1024) throw new IOException("共享安装标记过大。");
                oldMarker = await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false);
            }
            var temporary = ContentInstallFiles.Literal(destination, relative + ".install-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(source, temporary); if (await HashAsync(temporary, ct) != hash) throw new IOException("安装文件在发布时改变。");
                ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(target); File.Move(temporary, target, relative == ".shou/install.json");
                if (relative == ".shou/install.json") markerHash = hash; else added.Add((target, hash));
            }
            finally { VerifiedDownloader.RejectLink(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (complete) return;
        var changed = false;
        var marker = ContentInstallFiles.Literal(destination, ".shou/install.json");
        if (markerHash is not null)
        {
            if (!File.Exists(marker) || await HashAsync(marker, default) != markerHash) changed = true;
            else if (oldMarker is null) File.Delete(marker);
            else
            {
                var temporary = ContentInstallFiles.Literal(destination, ".shou/install.rollback-" + Guid.NewGuid().ToString("N"));
                try { await File.WriteAllBytesAsync(temporary, oldMarker); VerifiedDownloader.RejectLink(marker); File.Move(temporary, marker, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        foreach (var (path, hash) in added.AsEnumerable().Reverse())
        {
            if (!File.Exists(path)) continue;
            if (await HashAsync(path, default) == hash) File.Delete(path); else changed = true;
        }
        foreach (var path in directories.AsEnumerable().Reverse())
        { VerifiedDownloader.RejectLink(path); if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false); }
        if (changed) throw new IOException("回滚时检测到安装文件被外部修改，已保留修改内容，请检查共享目录。");
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { VerifiedDownloader.RejectLink(path); await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)); }
}
