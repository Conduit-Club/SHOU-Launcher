using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Content;

public sealed partial class ModrinthPackArchive
{
    public async Task<string> ExportAsync(GameInstance instance, ModrinthPackExportOptions options, string exportDirectory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 160 || options.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(options.VersionId) || options.VersionId.Length > 160 || options.VersionId.Any(char.IsControl))
            throw new ArgumentException("整合包名称与版本须为1–160字的单行文字。");
        var gameVersion = InstanceIdentity.GameVersion(instance);
        if (instance.Loader is not ("vanilla" or "fabric")) throw new NotSupportedException("首批.mrpack导出支持vanilla/Fabric实例。");
        if (instance.Loader == "fabric" && string.IsNullOrWhiteSpace(instance.LoaderVersion)) throw new InvalidDataException("Fabric实例缺少Loader版本。");
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        VerifiedDownloader.RejectLink(instance.GameDirectory); VerifiedDownloader.RejectLink(exportDirectory);
        if (IO.SafePath.IsWithin(IO.SafePath.Canonical(instance.GameDirectory), IO.SafePath.Canonical(exportDirectory))) throw new IOException("整合包输出目录不能位于实例内部。");
        var local = new Dictionary<string, string>(StringComparer.Ordinal); long overrideBytes = 0;
        void AddFile(string relative)
        {
            AllowedPath(relative); var full = ContentInstallFiles.Literal(instance.GameDirectory, relative);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaximumOverrideFileBytes) throw new IOException("选择的导出文件不存在或超过128 MiB限制。");
            if (local.TryAdd(Key(relative), relative)) overrideBytes = checked(overrideBytes + info.Length);
            else if (local[Key(relative)] != relative) throw new IOException("导出文件存在大小写或Unicode路径冲突。");
            if (local.Count > MaximumFiles || overrideBytes > MaximumOverrideBytes) throw new IOException("导出覆盖文件超过2048项或512 MiB限制。");
        }
        foreach (var requested in options.OverridePaths ?? [])
        {
            AllowedPath(requested, directory: true); var full = ContentInstallFiles.Literal(instance.GameDirectory, requested);
            if (Directory.Exists(full))
            {
                var pending = new Stack<string>(); pending.Push(requested); var directories = 0;
                while (pending.TryPop(out var current))
                {
                    ct.ThrowIfCancellationRequested(); if (++directories > MaximumEntries) throw new IOException("导出目录超过5000项限制。");
                    foreach (var child in Directory.EnumerateFileSystemEntries(ContentInstallFiles.Literal(instance.GameDirectory, current)))
                    {
                        var relative = Path.GetRelativePath(instance.GameDirectory, child).Replace('\\', '/');
                        _ = ContentInstallFiles.Literal(instance.GameDirectory, relative);
                        if (Directory.Exists(child)) pending.Push(relative); else AddFile(relative);
                    }
                }
            }
            else AddFile(requested);
        }
        var remote = new List<ModrinthPackFile>();
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); AllowedPath(file.RelativePath);
            var full = ContentInstallFiles.Literal(instance.GameDirectory, file.RelativePath);
            if (!await ContentInstallFiles.MatchesAsync(full, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new IOException("安装清单内的文件已改变，拒绝导出旧哈希：" + file.RelativePath);
            await using (var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                if (!Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装清单的SHA-512与实际文件不一致。");
            if (options.BundleInstalledFiles) AddFile(file.RelativePath);
            else if (file.DownloadUrl is { } url && !local.ContainsKey(Key(file.RelativePath))) remote.Add(new(file.RelativePath, file.Sha1, file.Sha512, file.Size, [url], file.Client, file.Server));
            else if (!local.ContainsKey(Key(file.RelativePath))) throw new InvalidOperationException("此实例含本地覆盖文件，请明确选入导出路径或启用打包已安装文件：" + file.RelativePath);
        }
        if (remote.Count > MaximumFiles || remote.Sum(f => f.Size) > MaximumDownloadBytes) throw new IOException("导出下载清单超过数量或总大小限制。");
        Directory.CreateDirectory(exportDirectory); VerifiedDownloader.RejectLink(exportDirectory);
        var final = ContentInstallFiles.Literal(exportDirectory, "pack-" + Guid.NewGuid().ToString("N") + ".mrpack"); var temporary = final + ".tmp";
        var published = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var relative in local.Values)
                    {
                        ct.ThrowIfCancellationRequested(); var full = ContentInstallFiles.Literal(instance.GameDirectory, relative);
                        var entry = zip.CreateEntry("client-overrides/" + relative, CompressionLevel.Optimal);
                        await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                        var length = input.Length; if (length > MaximumOverrideFileBytes) throw new IOException("导出文件在读取前超过大小限制。");
                        await using var target = entry.Open();
                        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                        var buffer = new byte[131072]; long copied = 0;
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (read == 0) break;
                            if ((copied += read) > length) throw new IOException("导出文件在读取时改变大小。");
                            sha1.AppendData(buffer, 0, read); sha512.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                        if (copied != length) throw new IOException("导出文件在读取时改变大小。");
                        var recorded = manifest.Files.FirstOrDefault(f => Key(f.RelativePath) == Key(relative));
                        if (recorded is not null && (Convert.ToHexStringLower(sha1.GetHashAndReset()) != recorded.Sha1.ToLowerInvariant() || Convert.ToHexStringLower(sha512.GetHashAndReset()) != recorded.Sha512.ToLowerInvariant()))
                            throw new IOException("打包文件在读取时改变，未生成整合包。");
                    }
                    var dependencies = new Dictionary<string, string> { ["minecraft"] = gameVersion };
                    if (instance.Loader == "fabric") dependencies.Add("fabric-loader", instance.LoaderVersion!);
                    var index = new { formatVersion = 1, game = "minecraft", versionId = options.VersionId, name = options.Name,
                        files = remote.Select(f => new { path = f.Path, hashes = new { sha1 = f.Sha1, sha512 = f.Sha512 }, env = new { client = f.Client, server = f.Server }, downloads = f.Downloads, fileSize = f.Size }), dependencies };
                    await using var indexStream = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal).Open();
                    await JsonSerializer.SerializeAsync(indexStream, index, cancellationToken: ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                if (output.Length > MaximumArchiveBytes) throw new IOException("导出的整合包超过256 MiB限制。");
            }
            ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(temporary); VerifiedDownloader.RejectLink(final);
            File.Move(temporary, final, false); published = true; return final;
        }
        finally { if (!published) { VerifiedDownloader.RejectLink(temporary); if (File.Exists(temporary)) File.Delete(temporary); } }
    }
}
