using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Content;

internal readonly record struct DataPackFormat(int Major, int Minor) : IComparable<DataPackFormat>
{
    public int CompareTo(DataPackFormat other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);
}
internal sealed record PackResource(string Hash, bool MergeableTag, int Priority = 0);
internal sealed record InspectedDatapack(string Revision, IReadOnlyDictionary<string, PackResource> Resources, long ExpandedBytes);
internal sealed record InspectedWorld(string Prefix, int DataVersion, string Version, IReadOnlyList<string> Files, string? Name = null);

internal static class WorldContentArchive
{
    internal static async Task<InspectedDatapack> DatapackAsync(string path, DataPackFormat format, CancellationToken ct)
    {
        using var zip = PackArchiveReader.Open(path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("数据包超过 64 MiB 限制。");
        var metadata = zip.Zip.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("数据包缺少根目录 pack.mcmeta。");
        var bytes = await PackArchiveReader.ReadEntryAsync(metadata, 65536, ct);
        var selected = Metadata(bytes, format);
        var resources = new Dictionary<string, PackResource>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in zip.Zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            ct.ThrowIfCancellationRequested(); expanded = checked(expanded + entry.Length);
            if (expanded > 128 * 1024 * 1024) throw new InvalidDataException("数据包展开超过 128 MiB 限制。");
            var content = await PackArchiveReader.ReadEntryAsync(entry, 64 * 1024 * 1024, ct);
            AddResource(resources, entry.FullName, content, selected);
        }
        zip.Verify(); return new(await zip.Sha256Async(ct), resources, expanded);
    }
    internal static async Task<InspectedDatapack> FolderDatapackAsync(string path, DataPackFormat format, CancellationToken ct)
    {
        var guards = new List<WorldPathSnapshot>(); var files = new List<string>();
        void Visit(string folder, int depth)
        {
            ct.ThrowIfCancellationRequested(); if (depth > 24 || files.Count > 5000) throw new IOException("已有数据包目录超过深度/文件预算。");
            guards.Add(WorldPathSnapshot.Capture(path, folder));
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                var guard = WorldPathSnapshot.Capture(path, child); guards.Add(guard);
                if (guard.Final.Directory) Visit(child, depth + 1); else files.Add(child);
            }
        }
        Visit(path, 0); if (files.Count > 5000) throw new IOException("已有数据包超过文件预算。");
        var metadata = await ReadFileAsync(path, ContentInstallFiles.Literal(path, "pack.mcmeta"), 65536, ct);
        var overlays = Metadata(metadata, format); var resources = new Dictionary<string, PackResource>(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long total = 0;
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var bytes = await ReadFileAsync(path, file, 64 * 1024 * 1024, ct); total += bytes.Length;
            if (total > 128 * 1024 * 1024) throw new IOException("已有数据包展开超过 128 MiB。");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative + "\0")); hash.AppendData(SHA256.HashData(bytes)); AddResource(resources, relative, bytes, overlays);
        }
        foreach (var guard in guards) guard.Verify(); return new(Convert.ToHexStringLower(hash.GetHashAndReset()), resources, total);
    }
    private static IReadOnlyList<string> Metadata(byte[] bytes, DataPackFormat target)
    {
        using var doc = PackArchiveReader.Json(bytes); var root = doc.RootElement;
        if (!root.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object) throw new InvalidDataException("pack.mcmeta 缺少 pack 对象。");
        if (root.TryGetProperty("features", out _)) throw new NotSupportedException("数据包声明实验特性，需要先明确处理世界特性，未安装。");
        if (!Range(pack, target, false)) throw new InvalidDataException($"数据包格式不兼容目标存档（{target.Major}.{target.Minor}）。");
        var prefixes = new List<string> { "" };
        if (root.TryGetProperty("overlays", out var overlays))
        {
            if (!overlays.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 64) throw new InvalidDataException("数据包 overlays 声明无效。");
            var seen = new HashSet<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                var directory = entry.GetProperty("directory").GetString() ?? "";
                ModrinthPackArchive.SafeRelative(directory);
                if (directory.Contains('/') || !seen.Add(directory)) throw new InvalidDataException("数据包 overlay 目录无效或重复。");
                if (Range(entry, target, true)) prefixes.Add(directory + "/");
            }
        }
        return prefixes;
    }
    private static DataPackFormat Number(JsonElement value, bool maximum)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var scalar) && scalar >= 0) return new(scalar, maximum ? int.MaxValue : 0);
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is 1 or 2)
        {
            var values = value.EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (values.Any(v => v < 0)) throw new InvalidDataException("数据包格式号无效。");
            return new(values[0], values.Length == 2 ? values[1] : maximum ? int.MaxValue : 0);
        }
        throw new InvalidDataException("数据包格式范围无效。");
    }
    private static bool Range(JsonElement metadata, DataPackFormat target, bool overlay)
    {
        DataPackFormat low, high;
        // Mojang 1.21.9: min/max formats include minor versions; scalar max accepts every minor.
        // https://www.minecraft.net/en-us/article/minecraft-java-edition-1-21-9
        if (metadata.TryGetProperty("min_format", out var min) && metadata.TryGetProperty("max_format", out var max))
        {
            low = Number(min, false); high = Number(max, true);
            if (!overlay && low.Major >= 82 && metadata.TryGetProperty("supported_formats", out _)) throw new InvalidDataException("此格式不允许 supported_formats。");
            if (!overlay && low.Major < 82 && (!metadata.TryGetProperty("pack_format", out _) || !metadata.TryGetProperty("supported_formats", out _))) throw new InvalidDataException("跨旧版本格式需要 pack_format 与 supported_formats。");
        }
        else
        {
            if (metadata.TryGetProperty("min_format", out _) || metadata.TryGetProperty("max_format", out _)) throw new InvalidDataException("格式上下界不完整。");
            if (target.Major >= 82) throw new InvalidDataException("目标版本要求 min_format / max_format。");
            if (metadata.TryGetProperty(overlay ? "formats" : "supported_formats", out var range))
            {
                if (range.ValueKind == JsonValueKind.Object) { low = Number(range.GetProperty("min_inclusive"), false); high = Number(range.GetProperty("max_inclusive"), true); }
                else if (range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2) { low = Number(range[0], false); high = Number(range[1], true); }
                else { low = Number(range, false); high = Number(range, true); }
            }
            else if (!overlay && metadata.TryGetProperty("pack_format", out var legacy)) { low = Number(legacy, false); high = Number(legacy, true); }
            else throw new InvalidDataException("数据包缺少兼容格式声明。");
        }
        if (low.CompareTo(high) > 0) throw new InvalidDataException("数据包格式上下界颠倒。");
        return low.CompareTo(target) <= 0 && high.CompareTo(target) >= 0;
    }
    private static void AddResource(Dictionary<string, PackResource> resources, string path, byte[] bytes, IReadOnlyList<string> prefixes)
    {
        for (var priority = 0; priority < prefixes.Count; priority++)
        {
            var prefix = prefixes[priority];
            if (!path.StartsWith(prefix + "data/", StringComparison.Ordinal)) continue;
            var key = path[prefix.Length..]; var merge = false;
            if (key.Split('/').Length < 4) continue;
            if (key.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '/' or '_' or '-' or '.'))) throw new InvalidDataException("数据包资源路径含无效命名字符：" + key);
            if (key.Split('/')[2] == "tags" && path.EndsWith(".json", StringComparison.Ordinal))
            {
                using var tag = PackArchiveReader.Json(bytes); merge = !tag.RootElement.TryGetProperty("replace", out var replace) || replace.ValueKind == JsonValueKind.False;
                merge &= tag.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array;
            }
            if (!resources.TryGetValue(key, out var previous) || previous.Priority <= priority) resources[key] = new(Convert.ToHexStringLower(SHA256.HashData(bytes)), merge, priority);
        }
    }
    internal static void CheckResources(Dictionary<string, (PackResource Resource, string Owner)> known, InspectedDatapack candidate, string owner, List<string> warnings)
    {
        foreach (var (path, resource) in candidate.Resources)
        {
            if (known.TryGetValue(path, out var prior) && prior.Owner != owner && prior.Resource.Hash != resource.Hash)
            {
                if (prior.Resource.MergeableTag && resource.MergeableTag) warnings.Add("标签按游戏规则合并：" + path);
                else throw new IOException($"数据包资源冲突：{path}（{prior.Owner} / {owner}），未覆盖。");
            }
            known[path] = (resource, owner);
        }
    }
    internal static async Task<byte[]> ReadFileAsync(string boundary, string path, int maximum, CancellationToken ct)
    {
        var guard = WorldPathSnapshot.Capture(boundary, path); if (guard.Final.Directory || guard.Final.Size > maximum) throw new IOException("文件超过读取预算。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (WorldFileIdentity.FromHandle(input.SafeFileHandle) != guard.Final) throw new IOException("文件身份改变。");
        var bytes = new byte[checked((int)guard.Final.Size)]; await input.ReadExactlyAsync(bytes, ct); guard.Verify(); return bytes;
    }
    private static bool FinderMetadata(string path) => path.Split('/')[^1] == ".DS_Store" || path.StartsWith("__MACOSX/", StringComparison.Ordinal) && path.Split('/')[^1].StartsWith("._", StringComparison.Ordinal);
    internal static async Task<InspectedWorld> WorldAsync(string path, CancellationToken ct, bool allowFinderMetadata = false)
    {
        using var zip = PackArchiveReader.Open(path);
        var entries = zip.Zip.Entries.Where(e => !allowFinderMetadata || !FinderMetadata(e.FullName)).ToArray();
        var levels = entries.Where(e => e.FullName == "level.dat" || e.FullName.EndsWith("/level.dat", StringComparison.Ordinal)).ToArray();
        if (levels.Length != 1) throw new InvalidDataException("世界 ZIP 必须包含唯一 Java level.dat。");
        var prefix = levels[0].FullName[..^"level.dat".Length];
        var files = entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName).ToArray();
        if (files.Any(p => !p.StartsWith(prefix, StringComparison.Ordinal))) throw new InvalidDataException("世界 ZIP 混有存档目录外的文件。");
        var bytes = await PackArchiveReader.ReadEntryAsync(levels[0], new NbtLimits().MaxInputBytes, ct);
        var data = BoundedNbt.Read(bytes, cancellationToken: ct).Root.Find("Data") ?? throw new InvalidDataException("不是 Java 世界。");
        var version = data.Find("Version")?.Find("Name"); var number = data.Find("DataVersion");
        if (version is not { Type: NbtType.String } || number is not { Type: NbtType.Int }) throw new InvalidDataException("世界版本身份缺失，未猜测兼容性。");
        foreach (var file in files) ValidateWorldPath(file[prefix.Length..]);
        zip.Verify(); return new(prefix, (int)number.Value, (string)version.Value, files.Select(p => p[prefix.Length..]).Where(p => p != "session.lock").ToArray(), data.Find("LevelName") is { Type: NbtType.String } name ? (string)name.Value : null);
    }
    private static void ValidateWorldPath(string path)
    {
        ModrinthPackArchive.SafeRelative(path);
        var parts = path.Split('/');
        if (parts.Any(p => p.StartsWith('.')) || parts[0].StartsWith("launcher_", StringComparison.OrdinalIgnoreCase) ||
            parts[0].ToLowerInvariant() is "accounts" or "accounts.json" or "credentials" or "credentials.json" or "tokens.json" or "passwords.json"
            || Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".so" or ".dylib" or ".ps1" or ".bat" or ".cmd" or ".sh" or ".jar" or ".key" or ".pem" or ".pfx") throw new InvalidDataException("世界 ZIP 含非存档或敏感文件：" + path);
    }
    internal static async Task ExtractWorldAsync(string path, string stage, InspectedWorld expected, string expectedSha256, CancellationToken ct)
    {
        using var zip = PackArchiveReader.Open(path);
        if (await zip.Sha256Async(ct) != expectedSha256) throw new IOException("世界 ZIP 自预检后改变。");
        foreach (var relative in expected.Files)
        {
            ct.ThrowIfCancellationRequested(); var target = ContentInstallFiles.Literal(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = await PackArchiveReader.ReadEntryAsync(zip.Zip.GetEntry(expected.Prefix + relative)!, PackArchiveReader.MaximumFileBytes, ct);
            await File.WriteAllBytesAsync(target, bytes, ct);
        }
        zip.Verify();
    }
    internal static async Task ValidateBundledWorldPacksAsync(string root, DataPackFormat format, CancellationToken ct, IReadOnlySet<string>? verifiedBuiltins = null)
    {
        var folder = ContentInstallFiles.Literal(root, "datapacks"); var resources = new Dictionary<string, (PackResource, string)>(); var warnings = new List<string>();
        if (Directory.Exists(folder))
        {
            var files = Directory.GetFileSystemEntries(folder); if (files.Length > 1024) throw new InvalidDataException("世界内数据包超过预算。"); long total = 0;
            foreach (var path in files)
            {
                _ = ContentInstallFiles.Literal(folder, Path.GetFileName(path));
                if (!Directory.Exists(path) && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var info = Directory.Exists(path) ? await FolderDatapackAsync(path, format, ct) : await DatapackAsync(path, format, ct);
                total += info.ExpandedBytes; if (total > 256 * 1024 * 1024) throw new InvalidDataException("世界内数据包超过展开预算。");
                CheckResources(resources, info, Path.GetFileName(path), warnings);
            }
        }
        var level = BoundedNbt.Read(await ReadFileAsync(root, ContentInstallFiles.Literal(root, "level.dat"), new NbtLimits().MaxInputBytes, ct), cancellationToken: ct).Root.Find("Data")!;
        if (level.Find("DataPacks")?.Find("Enabled") is { Type: NbtType.List, Value: NbtList active })
        {
            foreach (var pack in active.Items)
            {
                if (pack.Type != NbtType.String) throw new InvalidDataException("世界内数据包身份无效。"); var id = (string)pack.Value;
                if (id == "vanilla" || verifiedBuiltins?.Contains(id) == true) continue;
                if (!id.StartsWith("file/", StringComparison.Ordinal)) throw new NotSupportedException("世界依赖未核实的内建/模组数据包：" + id);
                var name = id[5..]; ModrinthPackArchive.SafeRelative(name);
                if (name.Contains('/') || !Path.Exists(ContentInstallFiles.Literal(folder, name))) throw new InvalidDataException("世界声明启用的数据包文件缺失。");
            }
        }
    }
}
