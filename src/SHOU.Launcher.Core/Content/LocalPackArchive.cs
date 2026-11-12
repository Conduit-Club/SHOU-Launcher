using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

public sealed record LocalPackDescriptor(LocalPackInspection Inspection,
    IReadOnlyList<CurseForgePackReference> CurseForgeFiles, IReadOnlyDictionary<string, string> OverrideEntries, bool CanResolveCurseForge);

/// <summary>Detects actual archive markers. Only CF client payload is staged here; genuine MRpack is delegated unchanged.</summary>
public sealed class LocalPackArchive
{
    public async Task<LocalPackDescriptor> ReadAsync(string path, CancellationToken ct = default)
    {
        using var archive = PackArchiveReader.Open(path); var sha256 = await archive.Sha256Async(ct).ConfigureAwait(false);
        var entries = archive.Zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var markers = new[] { "modrinth.index.json", "manifest.json", "mmc-pack.json", "shou-instance.json" }.Where(entries.ContainsKey).ToArray();
        if (markers.Length > 1 && markers.Contains("shou-instance.json", StringComparer.Ordinal))
        {
            // A root-override CF manifest may itself be game payload in a later exact SHOU export.
            using var metadata = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(entries["shou-instance.json"], 4 * 1024 * 1024, ct).ConfigureAwait(false));
            var value = metadata.RootElement;
            if (value.TryGetProperty("schemaVersion", out var schema) && schema.TryGetInt32(out var number) && number == 2 &&
                value.TryGetProperty("Files", out var list) && list.ValueKind == JsonValueKind.Array &&
                markers.Where(m => m != "shou-instance.json").All(m => list.EnumerateArray().Any(f => f.TryGetProperty("Path", out var pathValue) && pathValue.GetString() == m)))
                markers = ["shou-instance.json"];
        }
        if (markers.Length > 1) throw new InvalidDataException("ZIP 含多个不同格式的整合包标识，不能猜测导入方式。");
        var marker = markers.SingleOrDefault(); var prefix = "";
        if (marker is null)
        {
            var wrapped = entries.Keys.Where(k => k.Count(c => c == '/') == 1 &&
                new[] { "modrinth.index.json", "manifest.json", "mmc-pack.json", "shou-instance.json" }.Contains(k[(k.IndexOf('/') + 1)..], StringComparer.Ordinal)).ToArray();
            if (wrapped.Length > 1) throw new InvalidDataException("ZIP 含多个不同的整合包标识，不能猜测导入方式。");
            if (wrapped.Length == 1) { marker = wrapped[0]; prefix = marker[..(marker.IndexOf('/') + 1)]; }
        }
        var markerName = marker?[prefix.Length..];
        if (markerName == "modrinth.index.json")
        {
            archive.Verify(); var mrpack = await new ModrinthPackArchive().InspectAsync(path, ct).ConfigureAwait(false); archive.Verify();
            return new(new(LocalPackFormat.Modrinth, mrpack.Name, mrpack.GameVersion, mrpack.Loader, mrpack.LoaderVersion, sha256, mrpack.Warnings, mrpack.CanImport, mrpack.CapabilityReason, mrpack, mrpack.Overrides), [], new Dictionary<string, string>(), false);
        }
        if (marker is null) return Blocked(LocalPackFormat.Unknown, "未知 ZIP", sha256, "ZIP 未包含受支持的整合包格式标识；不会作为 SHOU 包或 MRpack 猜测导入。");
        using var json = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(entries[marker], 4 * 1024 * 1024, ct).ConfigureAwait(false)); var root = json.RootElement;
        if (marker.EndsWith("mmc-pack.json", StringComparison.Ordinal))
        {
            if (!entries.ContainsKey(prefix + "instance.cfg") || root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("MultiMC ZIP 缺少有效实例元数据。");
            var components = root.GetProperty("components"); if (components.ValueKind != JsonValueKind.Array || components.GetArrayLength() > 32) throw new InvalidDataException("MultiMC 组件清单无效。");
            var game = ""; var loader = "vanilla"; string? loaderVersion = null;
            foreach (var component in components.EnumerateArray()) { var uid = Text(component, "uid"); if (uid == "net.minecraft") game = Text(component, "version"); else if (uid == "net.fabricmc.fabric-loader") { loader = "fabric"; loaderVersion = Text(component, "version"); } }
            archive.Verify(); return Blocked(LocalPackFormat.MultiMC, "MultiMC 实例 ZIP", sha256, "已识别 MultiMC 格式；本轮导入支持 CurseForge ZIP 与 MRpack，MultiMC 仅预检，不执行其命令或迁移账户。", game, loader, loaderVersion);
        }
        if (markerName == "shou-instance.json")
        {
            if (prefix.Length > 0) return Blocked(LocalPackFormat.ShouExport, "SHOU 实例 ZIP", sha256, "SHOU 导出标识须位于归档根目录。");
            if (root.GetProperty("schemaVersion").GetInt32() == 2) return await ShouPackArchive.ReadAsync(archive, root, sha256, null, ct).ConfigureAwait(false);
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("SHOU 导出格式版本不受支持。");
            archive.Verify(); return Blocked(LocalPackFormat.ShouExport, Text(root, "Name"), sha256, "已识别 SHOU 实例导出；历史 v1 缺少精确文件校验清单，请使用新版重新导出。");
        }
        if (Text(root, "manifestType") != "minecraftModpack" || root.GetProperty("manifestVersion").GetInt32() != 1) throw new InvalidDataException("manifest.json 不是支持的 CurseForge Minecraft 整合包清单。");
        var minecraft = root.GetProperty("minecraft"); var gameVersion = IO.SafePath.Identifier(Text(minecraft, "version")); var name = Text(root, "name");
        var packVersion = root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? Text(root, "version") : null;
        int? recommendedMemory = minecraft.TryGetProperty("recommendedRam", out var ram) && ram.TryGetInt32(out var memory) && memory is >= 512 and <= 65536 ? memory : null;
        var loaderKind = "vanilla"; string? loaderVersionText = null; var supported = true; var loaders = minecraft.GetProperty("modLoaders");
        if (loaders.ValueKind != JsonValueKind.Array || loaders.GetArrayLength() > 16) throw new InvalidDataException("CurseForge 加载器列表无效。");
        var primary = loaders.EnumerateArray().Where(l => l.TryGetProperty("primary", out var flag) && flag.ValueKind == JsonValueKind.True).ToArray();
        if (loaders.GetArrayLength() > 0)
        {
            if (loaders.GetArrayLength() != 1 || primary.Length != 1) supported = false;
            else { var id = Text(primary[0], "id"); var dash = id.IndexOf('-'); loaderKind = dash > 0 ? id[..dash] : "unknown";
                if (dash > 0 && PackLoaderIdentity.Supported(loaderKind) && loaderKind != "vanilla") loaderVersionText = IO.SafePath.Identifier(id[(dash + 1)..]); else supported = false; }
        }
        var files = root.GetProperty("files"); if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > ModrinthPackArchive.MaximumFiles) throw new InvalidDataException($"CurseForge 文件清单超过 {ModrinthPackArchive.MaximumFiles} 项或类型无效。");
        var references = new List<CurseForgePackReference>(); var ids = new HashSet<int>();
        foreach (var file in files.EnumerateArray())
        {
            var project = file.GetProperty("projectID").GetInt32(); var id = file.GetProperty("fileID").GetInt32();
            if (project <= 0 || id <= 0 || !ids.Add(id)) throw new InvalidDataException("CurseForge 文件身份重复或无效。");
            references.Add(new(project, id, file.GetProperty("required").GetBoolean()));
        }
        var overrideRoot = root.TryGetProperty("overrides", out _) ? Text(root, "overrides", 512) : "overrides";
        if (overrideRoot is "." or "./") overrideRoot = "";
        else { overrideRoot = overrideRoot.TrimEnd('/'); ModrinthPackArchive.SafeRelative(overrideRoot); }
        var overridePrefix = prefix + (overrideRoot.Length == 0 ? "" : overrideRoot + "/");
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values.Where(e => e.FullName.StartsWith(overridePrefix, StringComparison.Ordinal)))
        { var relative = entry.FullName[overridePrefix.Length..]; PackPayloadPath.Validate(relative); overrides.Add(relative, entry.FullName); }
        if (overrides.Count + references.Count > ModrinthPackArchive.MaximumFiles) throw new InvalidDataException($"整合包客户端文件超过 {ModrinthPackArchive.MaximumFiles} 项。");
        PackArchiveReader.CheckTargets(overrides.Keys); archive.Verify();
        var warnings = new List<string> { "仅创建新的客户端实例；游戏本体将在首次启动时按官方安装流程准备。", "模组版本标签及元数据只做静态提示，完整依赖与实际兼容性由加载器最终验证。" };
        var reason = supported ? "尚需通过 CurseForge 官方 API 核实清单文件与下载可用性。" : "此 CurseForge ZIP 的加载器身份尚未支持；不猜测多个加载器组合。";
        return new(new(LocalPackFormat.CurseForge, name, gameVersion, loaderKind, loaderVersionText, sha256, warnings, false, reason, LocalFiles: overrides.Keys.ToArray(), PackVersion: packVersion, RecommendedMemoryMiB: recommendedMemory), references, overrides, supported);
    }
    public async Task<LocalPackInspection> StageCurseForgeAsync(string path, string expectedSha256, string directory,
        IReadOnlyList<CurseForgePackFile> files, Func<CurseForgePackFile, string, CancellationToken, Task> download, CancellationToken ct = default, IProgress<PackInstallProgress>? progress = null, int maximumConnections = 8)
    {
        if (!ContentInstallFiles.Hash(expectedSha256, 64)) throw new InvalidDataException("请先读取整合包并确认其 SHA-256。");
        VerifiedDownloader.RejectLink(directory); Directory.CreateDirectory(directory); if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("整合包暂存目录须为空。");
        var copyPath = ContentInstallFiles.Literal(Path.GetDirectoryName(Path.GetFullPath(directory))!, ".shou-local-input-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var source = PackArchiveReader.Open(path)) await source.CopyToAsync(copyPath, ct).ConfigureAwait(false);
            using var archive = PackArchiveReader.Open(copyPath); var actual = await archive.Sha256Async(ct).ConfigureAwait(false);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("ZIP 自预检后已改变，请重新预检。");
            var read = await ReadAsync(copyPath, ct).ConfigureAwait(false);
            if (read.Inspection.Format != LocalPackFormat.CurseForge || !read.CanResolveCurseForge) throw new NotSupportedException("此 ZIP 不是支持的 CurseForge 客户端包。");
            var required = read.CurseForgeFiles.Where(f => f.Required).ToArray();
            if (files.Count != required.Length || required.Any(r => files.Count(f => f.ProjectId == r.ProjectId && f.FileId == r.FileId) != 1)) throw new InvalidDataException("已解析文件未完整匹配 required 清单，未创建实例。");
            if (files.Select(f => ContentInstallFiles.PathKey(f.Path)).Distinct(StringComparer.Ordinal).Count() != files.Count || files.Sum(f => f.Size) > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("解析结果存在路径冲突或超过总大小预算。");
            PackArchiveReader.CheckTargets(files.Select(f => f.Path).Concat(read.OverrideEntries.Keys));
            var installed = new List<ContentInstalledFile>(); var done = 0; var total = files.Count + read.OverrideEntries.Count;
            progress?.Report(new("安装整合包文件", 0, total));
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(maximumConnections, 1, 64), CancellationToken = ct }, async (file, token) =>
            {
                var targetKind = file.Path.StartsWith("mods/", StringComparison.Ordinal) && file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mod"
                    : file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        ? file.Path.StartsWith("resourcepacks/", StringComparison.Ordinal) ? "resourcepack"
                            : file.Path.StartsWith("shaderpacks/", StringComparison.Ordinal) ? "shader"
                            : file.Path.StartsWith("datapacks/", StringComparison.Ordinal) ? "datapack"
                            : file.Path.StartsWith("saves/", StringComparison.Ordinal) ? "world" : null
                        : null;
                PackPayloadPath.Validate(file.Path);
                if (targetKind is null || file.Size is <= 0 or > PackArchiveReader.MaximumFileBytes || !ContentInstallFiles.Hash(file.Sha1, 40)) throw new InvalidDataException("解析结果的资源路径、大小或哈希无效。");
                var target = ContentInstallFiles.Literal(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await download(file, target, token).ConfigureAwait(false);
                if (!await ContentInstallFiles.MatchesAsync(target, file.Sha1, file.Size, token).ConfigureAwait(false)) throw new InvalidDataException("CurseForge 模组 SHA-1 或大小校验失败。");
                await using var bytes = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true); var sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(bytes, token).ConfigureAwait(false));
                if (file.Sha512 is not null && !sha512.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("同哈希回退模组 SHA-512 校验失败。");
                lock (installed) installed.Add(new("cf-" + file.ProjectId, "cf-" + file.FileId, targetKind, file.Path, file.Sha1, sha512, file.Size, file.FromModrinth ? file.DownloadUri.AbsoluteUri : null));
                progress?.Report(new("安装整合包文件", Interlocked.Increment(ref done), total, file.Path));
                foreach (var suffix in new[] { ".download.lock", ".part", ".part.resume.json" }) { var sidecar = ContentInstallFiles.Literal(directory, file.Path + suffix); if (File.Exists(sidecar)) File.Delete(sidecar); }

            }).ConfigureAwait(false);
            foreach (var (relative, entryName) in read.OverrideEntries)
            {
                var entry = archive.Zip.GetEntry(entryName) ?? throw new IOException("覆盖文件在暂存副本中缺失。");
                var bytes = await PackArchiveReader.ReadEntryAsync(entry, PackArchiveReader.MaximumFileBytes, ct).ConfigureAwait(false); var target = ContentInstallFiles.Literal(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false); installed.RemoveAll(f => ContentInstallFiles.PathKey(f.RelativePath) == ContentInstallFiles.PathKey(relative));
                progress?.Report(new("安装整合包文件", ++done, total, relative));
                installed.Add(new("cf-local", actual[..16], "pack", relative, Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.Length));
            }
            progress?.Report(new("安装整合包文件", total, total));
            archive.Verify(); ct.ThrowIfCancellationRequested(); await File.WriteAllBytesAsync(ContentInstallFiles.Literal(directory, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(new ContentInstallManifest(1, "unregistered", installed)), ct).ConfigureAwait(false);
            return read.Inspection;
        }
        finally { VerifiedDownloader.RejectLink(copyPath); if (File.Exists(copyPath)) File.Delete(copyPath); }
    }
    private static LocalPackDescriptor Blocked(LocalPackFormat format, string name, string sha, string reason, string game = "", string loader = "", string? loaderVersion = null)
        => new(new(format, name, game, loader, loaderVersion, sha, [], false, reason), [], new Dictionary<string, string>(), false);
    private static string Text(JsonElement value, string property, int maximum = 160)
    { var text = value.GetProperty(property).GetString() ?? ""; if (text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("整合包元数据文字字段无效。"); return text; }
}
