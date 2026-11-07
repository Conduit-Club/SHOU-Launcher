using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Content;

/// <summary>A portable client payload, with no launcher credentials, runtime, or executable installer.</summary>
internal static class ShouPackArchive
{
    private sealed record Entry(string Path, long Size, string Sha1, string Sha512);
    internal static bool Allowed(string path)
    {
        ModrinthPackArchive.SafeRelative(path);
        var parts = path.Split('/');
        if (parts.Any(p => p.StartsWith('.') || p.Contains("token", StringComparison.OrdinalIgnoreCase)
            || p.Contains("credential", StringComparison.OrdinalIgnoreCase) || p.Contains("password", StringComparison.OrdinalIgnoreCase)
            || p.Contains("account", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))) return false;
        if (path is "options.txt" or "servers.dat" or "icon.png" or "pack.png") return true;
        if (parts.Length < 2 || parts[0] is not ("mods" or "resourcepacks" or "shaderpacks" or "config" or "defaultconfigs" or "schematics" or "saves")) return false;
        return Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".command" or ".dll" or ".so" or ".dylib" or ".js" or ".vbs" or ".key" or ".pem" or ".pfx");
    }

    internal static async Task<string> ExportAsync(GameInstance instance, string outputDirectory, bool includeWorlds, CancellationToken ct, CurseForgeExportPlan? cf = null)
    {
        PackLoaderIdentity.ValidateShou(instance.Loader, instance.LoaderVersion);
        var gameVersion = InstanceIdentity.GameVersion(instance);
        if (instance.Loader == "fabric") IO.SafePath.Identifier(instance.LoaderVersion ?? throw new InvalidDataException("Fabric 版本身份缺失。"));
        if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Length > 160 || instance.Name.Any(char.IsControl)) throw new InvalidDataException("实例名称不适合导出，请先修改名称。");
        VerifiedDownloader.RejectLink(instance.GameDirectory); VerifiedDownloader.RejectLink(outputDirectory);
        if (IO.SafePath.IsWithin(IO.SafePath.Canonical(instance.GameDirectory), IO.SafePath.Canonical(outputDirectory))) throw new IOException("导出目录不能位于实例内部。");
        var files = new List<string>(); var sizes = new Dictionary<string, long>(StringComparer.Ordinal); long total = 0;
        void Visit(string relative, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (depth > 24) throw new IOException("实例目录嵌套过深。");
            var folder = ContentInstallFiles.Literal(instance.GameDirectory, relative); if (!Directory.Exists(folder)) return;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("导出不跟随实例链接。");
                var path = relative + "/" + entry.Name;
                if (entry is DirectoryInfo) Visit(path, depth + 1);
                else if (Allowed(path))
                {
                    if (((FileInfo)entry).Length > PackArchiveReader.MaximumFileBytes || (total = checked(total + ((FileInfo)entry).Length)) > PackArchiveReader.MaximumExpandedBytes - 4 * 1024 * 1024) throw new IOException("导出内容超过单文件 128 MiB 或合计 508 MiB 限制。");
                    files.Add(path); sizes.Add(path, ((FileInfo)entry).Length); if (files.Count > 2048) throw new IOException("导出文件超过 2048 项。");
                }
            }
        }
        if (cf is not null)
        {
            foreach (var path in cf.Paths)
            {
                ct.ThrowIfCancellationRequested(); var full = ContentInstallFiles.Literal(instance.GameDirectory, path);
                var size = new FileInfo(full).Length;
                if (size > PackArchiveReader.MaximumFileBytes || (total = checked(total + size)) > PackArchiveReader.MaximumExpandedBytes - 4 * 1024 * 1024) throw new IOException("导出内容超过大小限制。");
                files.Add(path); sizes.Add(path, size);
            }
        }
        else
        {
        foreach (var folder in new[] { "mods", "resourcepacks", "shaderpacks", "config", "defaultconfigs", "schematics" }.Concat(includeWorlds ? ["saves"] : Array.Empty<string>())) Visit(folder, 0);
        foreach (var name in new[] { "options.txt", "servers.dat", "icon.png", "pack.png" })
        {
            var path = ContentInstallFiles.Literal(instance.GameDirectory, name); if (!File.Exists(path)) continue;
            var size = new FileInfo(path).Length;
            if (size > PackArchiveReader.MaximumFileBytes || (total = checked(total + size)) > PackArchiveReader.MaximumExpandedBytes - 4 * 1024 * 1024) throw new IOException("导出文件超过大小限制。");
            files.Add(name); sizes.Add(name, size);
        }
        }
        if (files.Count > 2048) throw new IOException("导出文件超过 2048 项。");
        PackArchiveReader.CheckTargets(files); Directory.CreateDirectory(outputDirectory);
        var destination = ContentInstallFiles.Literal(outputDirectory, (cf is null ? "SHOU-" : "CF-") + IO.SafePath.Identifier(instance.Id) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".zip");
        var temp = ContentInstallFiles.Literal(outputDirectory, ".export-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true); var manifest = new List<Entry>();
                foreach (var path in files.Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested(); var full = ContentInstallFiles.Literal(instance.GameDirectory, path);
                    var guard = WorldPathSnapshot.Capture(instance.GameDirectory, full);
                    await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                    if (WorldFileIdentity.FromHandle(input.SafeFileHandle) != guard.Final || input.Length != sizes[path] || input.Length > PackArchiveReader.MaximumFileBytes) throw new IOException("导出文件身份发生变化。");
                    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                    await using var output = zip.CreateEntry((cf is null ? "" : "overrides/") + path, CompressionLevel.Fastest).Open(); var buffer = new byte[131072]; long length = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
                        length += count; if (length > guard.Final.Size) throw new IOException("导出文件长度发生变化。");
                        sha1.AppendData(buffer, 0, count); sha512.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    }
                    guard.Verify(); if (length != guard.Final.Size || WorldFileIdentity.FromHandle(input.SafeFileHandle) != guard.Final) throw new IOException("导出文件在读取时发生变化。");
                    manifest.Add(new(path, length, Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha512.GetHashAndReset())));
                }
                if (cf is null)
                {
                    await using var metadata = zip.CreateEntry("shou-instance.json").Open();
                    await JsonSerializer.SerializeAsync(metadata, new { schemaVersion = 2, instance.Name, GameVersion = gameVersion, instance.Loader, instance.LoaderVersion, Files = manifest }, cancellationToken: ct).ConfigureAwait(false);
                }
                else
                {
                    await using var metadata = zip.CreateEntry("manifest.json").Open();
                    await JsonSerializer.SerializeAsync(metadata, new { manifestType = "minecraftModpack", manifestVersion = 1, name = cf.Name, version = cf.Version, author = "", overrides = "overrides",
                        minecraft = new { version = gameVersion, modLoaders = instance.Loader == "vanilla" ? Array.Empty<object>() : new object[] { new { id = instance.Loader + "-" + instance.LoaderVersion, primary = true } } },
                        files = cf.References.Select(f => new { projectID = f.ProjectId, fileID = f.FileId, required = true }) }, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            using (var validate = PackArchiveReader.Open(temp)) validate.Verify();
            if (cf is not null) foreach (var guard in cf.Guards) guard.Verify();
            ct.ThrowIfCancellationRequested(); File.Move(temp, destination, false); return destination;
        }
        finally { VerifiedDownloader.RejectLink(temp); if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static async Task<LocalPackDescriptor> ReadAsync(PackArchiveReader archive, JsonElement root, string sha, string? stage, CancellationToken ct)
    {
        static string Text(JsonElement e, string key) { var s = e.GetProperty(key).GetString() ?? ""; if (s.Length is < 1 or > 160 || s.Any(char.IsControl)) throw new InvalidDataException("SHOU 元数据文字无效。"); return s; }
        var game = IO.SafePath.Identifier(Text(root, "GameVersion")); var loader = Text(root, "Loader");
        var loaderVersion = loader != "vanilla" ? IO.SafePath.Identifier(Text(root, "LoaderVersion")) : null;
        PackLoaderIdentity.ValidateShou(loader, loaderVersion);
        var files = root.GetProperty("Files").Deserialize<Entry[]>() ?? throw new InvalidDataException("SHOU 文件清单缺失。");
        if (files.Length > 2048) throw new InvalidDataException("SHOU 文件清单超过 2048 项。");
        PackArchiveReader.CheckTargets(files.Select(f => f.Path));
        var expected = files.Select(f => f.Path).Append("shou-instance.json").ToHashSet(StringComparer.Ordinal);
        var entries = archive.Zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
        if (expected.Count != files.Length + 1 || entries.Length != expected.Count || entries.Any(e => !expected.Contains(e.FullName))) throw new InvalidDataException("SHOU 内容与精确清单不一致。");
        var installed = new List<ContentInstalledFile>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!Allowed(file.Path) || file.Size is < 0 or > PackArchiveReader.MaximumFileBytes || !ContentInstallFiles.Hash(file.Sha1, 40) || !ContentInstallFiles.Hash(file.Sha512, 128)) throw new InvalidDataException("SHOU 文件路径或身份无效。");
            var entry = archive.Zip.GetEntry(file.Path)!; if (entry.Length != file.Size) throw new InvalidDataException("SHOU 文件大小不匹配。");
            var bytes = await PackArchiveReader.ReadEntryAsync(entry, PackArchiveReader.MaximumFileBytes, ct).ConfigureAwait(false);
            if (!Convert.ToHexStringLower(SHA1.HashData(bytes)).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase) || !Convert.ToHexStringLower(SHA512.HashData(bytes)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHOU 文件哈希不匹配。");
            if (stage is not null)
            {
                var target = ContentInstallFiles.Literal(stage, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
                installed.Add(new("shou-local", sha[..16], "pack", file.Path, file.Sha1, file.Sha512, file.Size));
            }
        }
        archive.Verify();
        if (stage is not null) await File.WriteAllBytesAsync(ContentInstallFiles.Literal(stage, ContentInstallFiles.ManifestName), JsonSerializer.SerializeToUtf8Bytes(new ContentInstallManifest(1, "unregistered", installed)), ct).ConfigureAwait(false);
        return new(new(LocalPackFormat.ShouExport, Text(root, "Name"), game, loader, loaderVersion, sha, ["导入到独立新实例；不迁移账户、Java 或原目录。"], true, LocalFiles: files.Select(f => f.Path).ToArray()), [], new Dictionary<string, string>(), false);
    }

    internal static async Task<LocalPackInspection> StageAsync(string path, string expectedSha, string directory, CancellationToken ct)
    {
        VerifiedDownloader.RejectLink(directory); Directory.CreateDirectory(directory); if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("SHOU 暂存目录须为空。");
        using var archive = PackArchiveReader.Open(path); var actual = await archive.Sha256Async(ct).ConfigureAwait(false);
        if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHOU 包自预检后已改变。");
        using var json = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(archive.Zip.GetEntry("shou-instance.json") ?? throw new InvalidDataException("SHOU 标识缺失。"), 4 * 1024 * 1024, ct).ConfigureAwait(false));
        if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 2) throw new InvalidDataException("SHOU 格式版本不支持。");
        return (await ReadAsync(archive, json.RootElement, actual, directory, ct).ConfigureAwait(false)).Inspection;
    }
}
