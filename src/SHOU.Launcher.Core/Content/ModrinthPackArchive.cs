using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

public sealed partial class ModrinthPackArchive
{
    public const long MaximumArchiveBytes = long.MaxValue, MaximumOverrideBytes = long.MaxValue;
    public const int MaximumEntries = PackArchiveReader.MaximumEntries, MaximumFiles = int.MaxValue;
    private const long MaximumOverrideFileBytes = long.MaxValue, MaximumDownloadBytes = long.MaxValue;
    private static readonly string[] DownloadSidecars = [".download.lock", ".part", ".part.resume.json"];
    private sealed record PackRead(ModrinthPackInspection Inspection, IReadOnlyDictionary<string, ZipArchiveEntry> Overrides);
    public async Task<ModrinthPackInspection> InspectAsync(string archivePath, CancellationToken ct = default)
    {
        await using var input = OpenArchive(archivePath);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)); input.Position = 0;
        ValidateDirectory(input); input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        return (await ReadAsync(zip, sha256, ct).ConfigureAwait(false)).Inspection;
    }
    private static FileStream OpenArchive(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择本机整合包的绝对路径。");
        VerifiedDownloader.RejectLink(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length is <= 0 or > MaximumArchiveBytes) { stream.Dispose(); throw new InvalidDataException("请选择非空的 ZIP 整合包。"); }
        return stream;
    }
    private static void ValidateDirectory(FileStream input) => PackArchiveReader.ValidateDirectory(input);
    public static string SafeRelative(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains('\\') || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || p.Any(c => char.IsControl(c) || ":<>|\"*?".Contains(c))))
            throw new InvalidDataException("整合包文件路径无效或越界。");
        return path;
    }
    private static string Key(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static void AllowedPath(string path, bool directory = false) => PackPayloadPath.Validate(path);
    private static async Task<byte[]> ReadBoundedAsync(Stream input, long maximum, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break;
            if (bytes.Length + count > maximum) throw new InvalidDataException("整合包条目解压超过大小限制。");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    private static void UniqueJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!seen.Add(property.Name)) throw new InvalidDataException("整合包清单存在重复JSON键。"); UniqueJson(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueJson(item);
    }
    private static string Text(JsonElement item, string key, int maximum = 160)
    {
        var value = item.GetProperty(key).GetString() ?? "";
        if (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl)) throw new InvalidDataException("整合包清单文字字段无效。");
        return value;
    }
    private static async Task<PackRead> ReadAsync(ZipArchive zip, string sha256, CancellationToken ct)
    {
        if (zip.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("ZIP条目数量超限。");
        var entryKeys = new HashSet<string>(StringComparer.Ordinal); var common = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var client = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal); long total = 0; var serverCount = 0;
        var indexes = zip.Entries.Where(e => e.FullName == "modrinth.index.json").ToArray();
        if (indexes.Length == 0) indexes = zip.Entries.Where(e => e.FullName.EndsWith("/modrinth.index.json", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 1).ToArray();
        if (indexes.Length != 1) throw new InvalidDataException("整合包须包含唯一的 modrinth.index.json。");
        var index = indexes[0]; var prefix = index.FullName[..^"modrinth.index.json".Length];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/'); var name = isDirectory ? entry.FullName[..^1] : entry.FullName;
            SafeRelative(name);
            if (!entryKeys.Add(Key(name)) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("ZIP含重复路径、符号链接或大小写/Unicode路径冲突。");
            if (isDirectory) { if (entry.Length != 0) throw new InvalidDataException("ZIP目录条目长度无效。"); continue; }
            if (entry == index) continue;
            if (entry.Length > MaximumOverrideFileBytes || checked(total += entry.Length) > MaximumOverrideBytes) throw new InvalidDataException("整合包解压长度超出系统可表示范围。");
            // Only format-defined override layers are game payload. Extra archive documentation is harmless.
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var scoped = name[prefix.Length..]; var slash = scoped.IndexOf('/'); if (slash < 0) continue;
            var layer = scoped[..slash]; var relative = scoped[(slash + 1)..]; SafeRelative(relative);
            if (layer == "server-overrides") { serverCount++; continue; }
            var target = layer switch { "overrides" => common, "client-overrides" => client, _ => null };
            if (target is null) continue;
            target.Add(Key(relative), entry);
        }
        using var json = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(index, int.MaxValue, ct).ConfigureAwait(false));
        UniqueJson(json.RootElement); var root = json.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1 || Text(root, "game") != "minecraft") throw new NotSupportedException("只支持Minecraft的Modrinth格式版本1。");
        var nameText = Text(root, "name"); var version = Text(root, "versionId");
        var dependencies = root.GetProperty("dependencies"); var gameVersion = IO.SafePath.Identifier(Text(dependencies, "minecraft"));
        var loader = "vanilla"; string? loaderVersion = null; string? unsupported = null; var loaders = 0;
        foreach (var property in dependencies.EnumerateObject())
        {
            if (property.Name == "minecraft") continue;
            loaders++; loaderVersion = IO.SafePath.Identifier(property.Value.GetString() ?? "");
            loader = property.Name switch { "fabric-loader" => "fabric", "forge" => "forge", "neoforge" or "neo-forge" => "neoforge", "quilt-loader" => "quilt", _ => "unknown" };
            if (!PackLoaderIdentity.Supported(loader)) unsupported = "此包的加载器尚未接入：" + property.Name;
        }
        if (loaders > 1) unsupported = "整合包同时声明多个加载器，首批不支持此组合。";
        var sourceFiles = root.GetProperty("files");
        if (sourceFiles.ValueKind != JsonValueKind.Array || sourceFiles.GetArrayLength() > MaximumFiles) throw new InvalidDataException($"整合包下载文件超过{MaximumFiles}项限制。");
        var files = new List<ModrinthPackFile>(); var fileKeys = new HashSet<string>(StringComparer.Ordinal); long downloadBytes = 0;
        foreach (var file in sourceFiles.EnumerateArray())
        {
            var path = Text(file, "path", 512); SafeRelative(path);
            var clientSide = "required"; var serverSide = "required";
            if (file.TryGetProperty("env", out var env)) { clientSide = Text(env, "client"); serverSide = Text(env, "server"); }
            if (clientSide is not ("required" or "optional" or "unsupported") || serverSide is not ("required" or "optional" or "unsupported")) throw new InvalidDataException("整合包客户端/服务器环境声明无效。");
            if (clientSide != "unsupported") AllowedPath(path);
            if (!fileKeys.Add(Key(path))) throw new InvalidDataException("下载清单存在重复或大小写/Unicode冲突路径。");
            var sha1 = Text(file.GetProperty("hashes"), "sha1"); var sha512 = Text(file.GetProperty("hashes"), "sha512");
            var size = file.GetProperty("fileSize").GetInt64();
            if (!ContentInstallFiles.Hash(sha1, 40) || !ContentInstallFiles.Hash(sha512, 128) || size is < 0 or > ContentInstallPlanner.MaximumTotalBytes || checked(downloadBytes += size) > MaximumDownloadBytes)
                throw new InvalidDataException("整合包文件哈希/大小无效或总下载超过4 GiB。");
            var urls = file.GetProperty("downloads").EnumerateArray().Select(u => u.GetString() ?? "").ToArray();
            if (urls.Length == 0 || urls.Any(u => u.Any(char.IsWhiteSpace) || !Uri.TryCreate(u, UriKind.Absolute, out var uri) || !PackPayloadPath.DownloadUri(uri)))
                throw new NotSupportedException("整合包下载来源须为 HTTP(S)，且不含 URL 账号密码或片段。");
            files.Add(new(path, sha1.ToLowerInvariant(), sha512.ToLowerInvariant(), size, urls, clientSide, serverSide));
        }
        foreach (var pair in client)
        {
            if (common.TryGetValue(pair.Key, out var prior) && prior.FullName[(prefix.Length + "overrides/".Length)..] != pair.Value.FullName[(prefix.Length + "client-overrides/".Length)..]) throw new InvalidDataException("overrides层存在大小写或Unicode路径歧义。");
            common[pair.Key] = pair.Value;
        }
        var overrides = common.Values.ToDictionary(e => { var name = e.FullName[prefix.Length..]; return name[(name.IndexOf('/') + 1)..]; }, e => e, StringComparer.Ordinal);
        if (overrides.Count > MaximumFiles) throw new InvalidDataException($"客户端overrides文件超过{MaximumFiles}项限制。");
        if (overrides.Keys.Any(p => files.Any(f => Key(f.Path) == Key(p) && f.Path != p))) throw new InvalidDataException("下载文件与overrides存在大小写或Unicode路径歧义。");
        foreach (var relative in overrides.Keys)
            if (!files.Any(f => Key(f.Path) == Key(relative) && f.Client == "unsupported")) AllowedPath(relative);
        var clientPaths = files.Where(f => f.Client != "unsupported").Select(f => Key(f.Path)).Concat(overrides.Keys.Select(Key)).ToHashSet(StringComparer.Ordinal);
        if (clientPaths.Count > MaximumFiles) throw new InvalidDataException($"整合包客户端文件超过{MaximumFiles}项限制。");
        PackArchiveReader.CheckTargets(clientPaths);
        if (files.Where(f => f.Client != "unsupported").Any(f => DownloadSidecars.Any(suffix => clientPaths.Contains(Key(f.Path + suffix)))))
            throw new InvalidDataException("整合包路径与下载器暂存侧文件冲突。");
        var warnings = new List<string> { "导入完整整合包内容并创建新实例；游戏本体将在首次启动时按原安装流程下载。" };
        if (serverCount > 0 || files.Any(f => f.Client == "unsupported")) warnings.Add("服务器专用文件与server-overrides将跳过；不会装入客户端。");
        if (files.Any(f => f.Client == "optional")) warnings.Add("客户端可选文件默认不安装，请明确勾选需要的文件。");
        return new(new(nameText, version, gameVersion, loader, loaderVersion, sha256, files, overrides.Keys.ToArray(), warnings, unsupported is null, unsupported), overrides);
    }
    public async Task<ModrinthPackInspection> StageAsync(string archivePath, string expectedSha256, string directory,
        IReadOnlyList<string>? optionalFiles, Func<ModrinthPackFile, string, CancellationToken, Task> download, CancellationToken ct = default, IProgress<PackInstallProgress>? progress = null, int maximumConnections = 8)
    {
        if (!ContentInstallFiles.Hash(expectedSha256, 64)) throw new InvalidDataException("请先读取整合包并确认其文件身份。");
        VerifiedDownloader.RejectLink(directory); Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("整合包暂存目录必须为空。");
        var archiveCopy = ContentInstallFiles.Literal(Path.GetDirectoryName(Path.GetFullPath(directory))!, ".shou-pack-input-" + Guid.NewGuid().ToString("N") + ".mrpack");
        try
        {
            PackDiskSpace.Require(directory, new FileInfo(archivePath).Length);
            await using (var source = OpenArchive(archivePath))
            await using (var copy = new FileStream(archiveCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(copy, ct).ConfigureAwait(false);
            await using var input = OpenArchive(archiveCopy);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
            if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("整合包自读取后已改变，请重新读取确认。");
            input.Position = 0; ValidateDirectory(input); input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            var read = await ReadAsync(zip, digest, ct).ConfigureAwait(false); var inspection = read.Inspection;
            if (!inspection.CanImport) throw new NotSupportedException(inspection.CapabilityReason);
            var selected = (optionalFiles ?? []).ToHashSet(StringComparer.Ordinal);
            if (selected.Any(p => !inspection.Files.Any(f => f.Path == p && f.Client == "optional"))) throw new InvalidDataException("可选文件选择已失效或包含服务器文件。");
            var installed = new List<ContentInstalledFile>(); var done = 0;
            var selectedFiles = inspection.Files.Where(f => f.Client == "required" || f.Client == "optional" && selected.Contains(f.Path)).ToArray();
            PackDiskSpace.Require(directory, checked(selectedFiles.Sum(f => f.Size) + read.Overrides.Values.Sum(e => e.Length)));
            var total = selectedFiles.Length + read.Overrides.Count; progress?.Report(new("安装整合包文件", 0, total));
            await Parallel.ForEachAsync(selectedFiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(maximumConnections, 1, 64), CancellationToken = ct }, async (file, token) =>
            {
                token.ThrowIfCancellationRequested(); var path = ContentInstallFiles.Literal(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await download(file, path, token).ConfigureAwait(false);
                if (!await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, token).ConfigureAwait(false)) throw new InvalidDataException("整合包文件SHA-1校验失败。");
                await using var bytes = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                var sha512 = Convert.ToHexStringLower(await SHA512.HashDataAsync(bytes, token).ConfigureAwait(false));
                if (sha512 != file.Sha512) throw new InvalidDataException("整合包文件SHA-512校验失败。");
                foreach (var suffix in DownloadSidecars)
                {
                    var sidecar = path + suffix; VerifiedDownloader.RejectLink(sidecar);
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
                progress?.Report(new("安装整合包文件", Interlocked.Increment(ref done), total, file.Path));
                lock (installed) installed.Add(new("mrpack", inspection.VersionId, "pack", file.Path, file.Sha1, sha512, file.Size, file.Downloads[0], file.Client, file.Server));

            }).ConfigureAwait(false);
            foreach (var (relative, entry) in read.Overrides)
            {
                var declared = inspection.Files.FirstOrDefault(f => Key(f.Path) == Key(relative));
                if (declared is not null && (declared.Client == "unsupported" || declared.Client == "optional" && !selected.Contains(declared.Path))) continue;
                ct.ThrowIfCancellationRequested(); var path = ContentInstallFiles.Literal(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) File.Delete(path);
                var streamed = await PackArchiveReader.StreamEntryAsync(entry, path, ct).ConfigureAwait(false);
                installed.RemoveAll(f => Key(f.RelativePath) == Key(relative));
                progress?.Report(new("安装整合包文件", ++done, total, relative));
                installed.Add(new("mrpack-local", inspection.VersionId, "pack", relative, streamed.Sha1, streamed.Sha512, streamed.Size));
            }
            progress?.Report(new("安装整合包文件", total, total));
            ct.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(ContentInstallFiles.Literal(directory, ContentInstallFiles.ManifestName),
                ContentInstallFiles.SerializeManifest(new ContentInstallManifest(1, "unregistered", installed)), ct).ConfigureAwait(false);
            return inspection;
        }
        finally { VerifiedDownloader.RejectLink(archiveCopy); if (File.Exists(archiveCopy)) File.Delete(archiveCopy); }
    }
}
