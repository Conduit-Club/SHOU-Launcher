using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Content;

internal sealed record CurseForgeExportPlan(string Name, string Version, IReadOnlyList<CurseForgePackReference> References, IReadOnlySet<string> Paths, IReadOnlyList<WorldPathSnapshot> Guards);

internal static class CurseForgePackExport
{
    internal static async Task<CurseForgeExportPlan> PlanAsync(GameInstance instance, ModrinthPackExportOptions options, CancellationToken ct)
    {
        foreach (var text in new[] { options.Name, options.VersionId })
            if (string.IsNullOrWhiteSpace(text) || text.Length > 160 || text.Any(char.IsControl)) throw new ArgumentException("整合包名称和版本须为 1–160 字的单行文字。");
        PackLoaderIdentity.Validate(instance.Loader, instance.LoaderVersion);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<CurseForgePackReference>();
        var guards = new List<WorldPathSnapshot>();
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!LocalPackArchive.AllowedPayload(file.RelativePath) || !ShouPackArchive.Allowed(file.RelativePath)) throw new InvalidDataException("安装清单含不可导出的文件。");
            var full = ContentInstallFiles.Literal(instance.GameDirectory, file.RelativePath);
            var guard = WorldPathSnapshot.Capture(instance.GameDirectory, full); guards.Add(guard);
            if (!await ContentInstallFiles.MatchesAsync(full, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new IOException("安装文件已改变，请先重新核对资源。");
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (!Convert.ToHexStringLower(await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装文件 SHA-512 不匹配。");
            guard.Verify();
            if (file.Kind is "mod" or "resourcepack" && file.ProjectId.StartsWith("cf-", StringComparison.Ordinal) && int.TryParse(file.ProjectId[3..], out var project) && project > 0
                && file.ReleaseId.StartsWith("cf-", StringComparison.Ordinal) && int.TryParse(file.ReleaseId[3..], out var id) && id > 0)
                references.Add(new(project, id, true));
            else if (options.BundleInstalledFiles) paths.Add(file.RelativePath);
            else if (!(options.OverridePaths ?? []).Any(p => file.RelativePath == p || file.RelativePath.StartsWith(p + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException("包含没有 CurseForge 文件身份的资源，请明确选择打包本地资源。");
        }
        var count = 0;
        void Visit(string path, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (++count > 5000 || depth > 24) throw new IOException("导出路径超过数量或深度限制。");
            var full = ContentInstallFiles.Literal(instance.GameDirectory, path);
            if (Directory.Exists(full))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) Visit(Path.GetRelativePath(instance.GameDirectory, child).Replace('\\', '/'), depth + 1);
            }
            else if (File.Exists(full) && LocalPackArchive.AllowedPayload(path) && ShouPackArchive.Allowed(path)) paths.Add(path);
            else if (!File.Exists(full)) throw new InvalidDataException("导出路径不存在：" + path);
        }
        foreach (var path in options.OverridePaths ?? [])
        {
            var full = ContentInstallFiles.Literal(instance.GameDirectory, path);
            var scope = Directory.Exists(full) ? path + "/__scope" : path;
            if (!LocalPackArchive.AllowedPayload(scope) || !ShouPackArchive.Allowed(scope)) throw new InvalidDataException("导出路径不受支持：" + path);
            Visit(path, 0);
        }
        // CF-owned files are references even when their containing folder was explicitly selected.
        foreach (var file in manifest.Files.Where(f => references.Any(r => f.ProjectId == "cf-" + r.ProjectId && f.ReleaseId == "cf-" + r.FileId))) paths.Remove(file.RelativePath);
        if (references.Count > 512 || references.Select(r => r.FileId).Distinct().Count() != references.Count) throw new InvalidDataException("CurseForge 文件身份重复或超过 512 项。");
        return new(options.Name, options.VersionId, references, paths, guards);
    }
}
