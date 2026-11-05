using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Content;

public static class ContentInstallFiles
{
    public const string ManifestName = ".shou-content-manifest.json";
    public static string PathKey(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    public static void ValidateFile(ContentFile file)
    {
        if (file.FileName.Length is 0 or > 200 || file.FileName.Any(c => char.IsControl(c) || c is '/' or '\\' or ':') || file.FileName is "." or ".." ||
            !Hash(file.Sha1, 40) || file.Size is < 0 or > ContentInstallPlanner.MaximumTotalBytes || !AllowedUri(file.Uri)) throw new InvalidDataException("资源文件身份、来源或大小无效。");
    }
    public static bool Hash(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    public static bool AllowedUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IsDefaultPort;
    public static string Literal(string root, string relative)
    {
        if (relative.Length is 0 or > 512 || relative.Contains('\\') || relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => char.IsControl(c) || c == ':'))) throw new InvalidDataException("文件路径无效。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!SafePath.IsWithin(Path.GetFullPath(root), full)) throw new IOException("文件路径越界。");
        VerifiedDownloader.RejectLink(full); return full;
    }
    public static async Task<ContentInstallManifest> ReadManifestAsync(GameInstance instance, CancellationToken ct = default)
    {
        var path = Literal(instance.GameDirectory, ManifestName);
        if (!File.Exists(path)) return new(1, instance.Id, []);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("安装清单过大，原文件保留。");
        var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        if (manifest is null || manifest.FormatVersion != 1 || manifest.InstanceId != instance.Id || manifest.Files is null || manifest.Files.Count > 4096 ||
            manifest.Files.Select(f => PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count) throw new InvalidDataException("安装清单无效，原文件保留。");
        foreach (var item in manifest.Files)
        {
            _ = Literal(instance.GameDirectory, item.RelativePath);
            if (!Hash(item.Sha1, 40) || !Hash(item.Sha512, 128) || item.Size is < 0 or > ContentInstallPlanner.MaximumTotalBytes ||
                item.DownloadUrl is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedUri(uri))) throw new InvalidDataException("安装清单文件身份无效。");
        }
        return manifest;
    }
    public static async Task<bool> MatchesAsync(string path, string sha1, long size, CancellationToken ct)
    {
        VerifiedDownloader.RejectLink(path);
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length != size) return false;
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task CheckConflictsAsync(string root, IReadOnlyList<ContentInstallFile> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var target = Literal(root, file.RelativePath);
            var exists = File.Exists(target);
            if (Directory.Exists(target) || exists && !await MatchesAsync(target, file.File.Sha1, file.File.Size, ct).ConfigureAwait(false)) throw new IOException("目标已有不同文件，未覆盖：" + file.RelativePath);
            var parent = Path.GetDirectoryName(target)!;
            if (!exists && Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent).Any(p => PathKey(Path.GetFileName(p)) == PathKey(Path.GetFileName(target)) && Path.GetFileName(p) != Path.GetFileName(target))) throw new IOException("目标存在仅大小写或Unicode形式不同的文件：" + file.RelativePath);
        }
    }
    public static async Task<ContentInstallResult> InstallAsync(GameInstance instance, ContentInstallPlan plan,
        Func<ContentFile, string, CancellationToken, Task> download, CancellationToken ct = default)
    {
        if (plan.InstanceId != instance.Id || plan.Files.Count is 0 or > ContentInstallPlanner.MaximumProjects || plan.Files.Select(f => PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != plan.Files.Count) throw new InvalidDataException("安装计划身份、文件数量或路径冲突无效。");
        VerifiedDownloader.RejectLink(instance.GameDirectory);
        var previous = await ReadManifestAsync(instance, ct).ConfigureAwait(false);
        var manifestPath = Literal(instance.GameDirectory, ManifestName);
        var previousBytes = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false) : null;
        await CheckConflictsAsync(instance.GameDirectory, plan.Files, ct).ConfigureAwait(false);
        var stage = Literal(instance.GameDirectory, ".shou-content-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var added = new List<ContentInstallFile>(); var completed = new List<ContentInstalledFile>(); var committed = false;
        try
        {
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested(); ValidateFile(file.File);
                var staging = Literal(stage, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                await download(file.File, staging, ct).ConfigureAwait(false);
                if (!await MatchesAsync(staging, file.File.Sha1, file.File.Size, ct).ConfigureAwait(false)) throw new InvalidDataException("资源文件校验失败，实例原文件未改变。");
                await using var input = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                var sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
                completed.Add(new(file.ProjectId, file.ReleaseId, file.Kind, file.RelativePath, file.File.Sha1, sha512, file.File.Size, file.File.Uri.AbsoluteUri));
            }
            await CheckConflictsAsync(instance.GameDirectory, plan.Files, ct).ConfigureAwait(false);
            var merged = previous.Files.Where(f => completed.All(n => PathKey(n.RelativePath) != PathKey(f.RelativePath))).Concat(completed).ToArray();
            if (merged.Length > 4096) throw new InvalidDataException("实例安装清单超过4096条限制。");
            var nextManifest = Literal(stage, ManifestName);
            await File.WriteAllBytesAsync(nextManifest, JsonSerializer.SerializeToUtf8Bytes(new ContentInstallManifest(1, instance.Id, merged)), ct).ConfigureAwait(false);
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested(); var target = Literal(instance.GameDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); target = Literal(instance.GameDirectory, file.RelativePath);
                if (File.Exists(target)) { if (!await MatchesAsync(target, file.File.Sha1, file.File.Size, ct).ConfigureAwait(false)) throw new IOException("目标在安装时改变，已停止。"); continue; }
                File.Move(Literal(stage, file.RelativePath), target, false); added.Add(file);
            }
            ct.ThrowIfCancellationRequested(); _ = Literal(instance.GameDirectory, ManifestName);
            var current = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false) : null;
            if (!(previousBytes is null ? current is null : current is not null && previousBytes.AsSpan().SequenceEqual(current))) throw new IOException("安装清单已被其他操作改变，已回滚新增文件。");
            ct.ThrowIfCancellationRequested(); _ = Literal(instance.GameDirectory, ManifestName);
            File.Move(nextManifest, manifestPath, true); committed = true;
            return new(instance.Id, manifestPath, completed, added.Count, plan.Warnings);
        }
        finally
        {
            if (!committed) foreach (var file in added)
            {
                var target = Literal(instance.GameDirectory, file.RelativePath);
                if (await MatchesAsync(target, file.File.Sha1, file.File.Size, CancellationToken.None).ConfigureAwait(false)) File.Delete(target);
            }
            VerifiedDownloader.RejectLink(stage); if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
