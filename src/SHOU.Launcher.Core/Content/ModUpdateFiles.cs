using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

/// <summary>Downloads before publishing; retains the original resource and manifest in an instance-local backup.</summary>
public static class ModUpdateFiles
{
    public static async Task<InstanceModUpdateResult> ApplyAsync(GameInstance instance, ContentInstalledFile old,
        ContentInstallPlan plan, Func<ContentFile, string, CancellationToken, Task> download, Action validateTarget, CancellationToken ct = default)
    {
        var folder = ContentInstallPlanner.Folder(old.Kind); var resource = old.Kind is "resourcepack" or "shader";
        if (plan.InstanceId != instance.Id || plan.Files.Count is 0 or > 64 || plan.Files.Any(f =>
            (!resource && f.Kind != "mod") || !f.RelativePath.StartsWith(ContentInstallPlanner.Folder(f.Kind) + "/", StringComparison.Ordinal))) throw new InvalidDataException("资源更新计划无效。");
        if (!old.RelativePath.StartsWith(folder + "/", StringComparison.Ordinal) || old.RelativePath[(folder.Length + 1)..].Contains('/')) throw new InvalidDataException("原资源路径无效。");
        var rootFile = plan.Files.SingleOrDefault(f => !f.Dependency && f.ProjectId == old.ProjectId && f.Kind == old.Kind) ?? throw new InvalidDataException("更新缺少唯一主资源。");
        if (resource && rootFile.RelativePath != old.RelativePath) throw new InvalidDataException("资源更新须保留原文件名及其启用引用。");
        var root = instance.GameDirectory; var manifestPath = ContentInstallFiles.Literal(root, ContentInstallFiles.ManifestName);
        var previous = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        var previousBytes = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false) : null;
        var oldPath = ContentInstallFiles.Literal(root, old.RelativePath);
        async Task VerifyOld(CancellationToken token)
        {
            if (!await ContentInstallFiles.MatchesAsync(oldPath, old.Sha1, old.Size, token).ConfigureAwait(false)) throw new IOException("原模组已改变，请重新检查。");
            await using var input = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (Convert.ToHexStringLower(await SHA512.HashDataAsync(input, token).ConfigureAwait(false)) != old.Sha512) throw new IOException("原模组 SHA-512 已改变。");
        }
        async Task VerifyManifest(CancellationToken token)
        {
            var now = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false) : null;
            if (!(previousBytes is null ? now is null : now is not null && previousBytes.AsSpan().SequenceEqual(now))) throw new IOException("安装清单已改变，请重新检查。");
        }
        await VerifyOld(ct).ConfigureAwait(false); validateTarget();
        var disabledSuffix = old.RelativePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? ".disabled" : old.RelativePath.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? ".old" : "";
        var disabled = disabledSuffix.Length > 0;
        var files = plan.Files.Select(f => disabled && f == rootFile ? f with { RelativePath = f.RelativePath + ".disabled" } : f).ToArray();
        if (files.Select(f => ContentInstallFiles.PathKey(f.RelativePath)).Distinct(StringComparer.Ordinal).Count() != files.Length) throw new InvalidDataException("更新目标文件路径冲突。");
        var untouched = files.Where(f => ContentInstallFiles.PathKey(f.RelativePath) != ContentInstallFiles.PathKey(old.RelativePath)).ToArray();
        await ContentInstallFiles.CheckConflictsAsync(root, untouched, ct).ConfigureAwait(false);
        var suffix = Guid.NewGuid().ToString("N"); var stage = ContentInstallFiles.Literal(root, (resource ? ".shou-resource-update-stage-" : ".shou-mod-update-stage-") + suffix);
        var backup = ContentInstallFiles.Literal(root, (resource ? ".shou-resource-backups/" : ".shou-mod-backups/") + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + suffix);
        var backupOld = ContentInstallFiles.Literal(backup, old.RelativePath);
        var completed = new List<ContentInstalledFile>(); var published = new List<ContentInstalledFile>(); var movedOld = false; var committed = false;
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested(); ContentInstallFiles.ValidateFile(file.File);
                var target = ContentInstallFiles.Literal(stage, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await download(file.File, target, ct).ConfigureAwait(false);
                if (!await ContentInstallFiles.MatchesAsync(target, file.File.Sha1, file.File.Size, ct).ConfigureAwait(false)) throw new InvalidDataException("更新文件校验失败，原文件保留。");
                await using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                var primary = !file.Dependency && file.ProjectId == old.ProjectId;
                completed.Add(new(file.ProjectId, file.ReleaseId, file.Kind, file.RelativePath, file.File.Sha1, Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false)), file.File.Size, file.File.Uri.AbsoluteUri, primary ? old.Client : "required", primary ? old.Server : "unsupported"));
            }
            var oldEnabledPath = disabled ? old.RelativePath[..^disabledSuffix.Length] : old.RelativePath;
            var merged = previous.Files.Where(f => f.RelativePath != old.RelativePath && f.RelativePath != oldEnabledPath && f.RelativePath != oldEnabledPath + ".disabled" && completed.All(n => ContentInstallFiles.PathKey(n.RelativePath) != ContentInstallFiles.PathKey(f.RelativePath))).Concat(completed).ToArray();
            var nextManifest = ContentInstallFiles.Literal(stage, ContentInstallFiles.ManifestName);
            await File.WriteAllBytesAsync(nextManifest, ContentInstallFiles.SerializeManifest(new(1, instance.Id, merged)), ct).ConfigureAwait(false);
            await VerifyOld(ct).ConfigureAwait(false); await VerifyManifest(ct).ConfigureAwait(false); validateTarget();
            await ContentInstallFiles.CheckConflictsAsync(root, untouched, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(backupOld)!);
            if (previousBytes is not null) await File.WriteAllBytesAsync(ContentInstallFiles.Literal(backup, ContentInstallFiles.ManifestName), previousBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(oldPath, backupOld, false); movedOld = true;
            foreach (var file in completed)
            {
                ct.ThrowIfCancellationRequested(); var target = ContentInstallFiles.Literal(root, file.RelativePath);
                if (File.Exists(target))
                {
                    if (!await ContentInstallFiles.MatchesAsync(target, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new IOException("更新目标在下载期间改变。");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(ContentInstallFiles.Literal(stage, file.RelativePath), target, false); published.Add(file);
            }
            await VerifyManifest(ct).ConfigureAwait(false); validateTarget(); ct.ThrowIfCancellationRequested();
            File.Move(nextManifest, manifestPath, true); committed = true;
            return new(backup, completed.Count);
        }
        finally
        {
            if (!committed)
            {
                foreach (var file in published)
                {
                    var target = ContentInstallFiles.Literal(root, file.RelativePath);
                    if (await ContentInstallFiles.MatchesAsync(target, file.Sha1, file.Size, CancellationToken.None).ConfigureAwait(false)) File.Delete(target);
                }
                if (movedOld)
                {
                    _ = ContentInstallFiles.Literal(root, old.RelativePath);
                    if (File.Exists(oldPath)) throw new IOException("回滚目标被外部改变，原模组已保留在：" + backup);
                    File.Move(backupOld, oldPath, false);
                }
            }
            VerifiedDownloader.RejectLink(stage); if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
