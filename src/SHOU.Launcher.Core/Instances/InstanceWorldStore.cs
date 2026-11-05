using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

public sealed record InstanceWorldLimits(int MaxServers = 2048, int MaxWorlds = 1024, int MaxDatapacks = 1024,
    int MaxPackBytes = 64 * 1024 * 1024, long MaxPackExpandedBytes = 256 * 1024 * 1024,
    int MaxPackEntries = 10000, int MaxPackEntryBytes = 64 * 1024 * 1024, int MaxCompressionRatio = 200);

/// <summary>Only explicitly selected isolated instance paths are passed by the service. Existing NBT is retained as a tree.</summary>
public sealed class InstanceWorldStore
{
    private readonly string dataRoot, gameRoot;
    private readonly NbtLimits nbtLimits;
    private readonly InstanceWorldLimits limits;
    private readonly WorldFileIdentity dataIdentity, gameIdentity;
    private readonly SemaphoreSlim mutation = new(1, 1);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public InstanceWorldStore(string isolatedDataRoot, string gameDirectory, NbtLimits? nbtLimits = null, InstanceWorldLimits? limits = null)
    {
        if (!Path.IsPathFullyQualified(isolatedDataRoot) || !Path.IsPathFullyQualified(gameDirectory)) throw new IOException("Absolute isolated paths are required.");
        dataRoot = SafePath.Canonical(isolatedDataRoot); gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        if (!SafePath.IsWithin(dataRoot, gameRoot) || string.Equals(dataRoot, gameRoot, PathComparison)
            || !string.Equals(SafePath.Canonical(gameRoot), gameRoot, PathComparison)) throw new IOException("Instance directory must be a direct physical descendant of the isolated data root.");
        dataIdentity = WorldFileIdentity.AtPath(dataRoot); gameIdentity = WorldFileIdentity.AtPath(gameRoot);
        if (!dataIdentity.Directory || !gameIdentity.Directory) throw new IOException("Instance and data directories must exist.");
        this.nbtLimits = nbtLimits ?? new(); this.limits = limits ?? new();
        if (this.limits.MaxServers <= 0 || this.limits.MaxWorlds <= 0 || this.limits.MaxDatapacks <= 0 || this.limits.MaxPackBytes <= 0
            || this.limits.MaxPackExpandedBytes <= 0 || this.limits.MaxPackEntries <= 0 || this.limits.MaxPackEntryBytes <= 0 || this.limits.MaxCompressionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _ = Capture(gameRoot);
    }

    public async Task<InstanceServerList> GetServersAsync(CancellationToken cancellationToken = default)
    {
        var raw = await ReadOptionalAsync(Child("servers.dat"), nbtLimits.MaxInputBytes, cancellationToken);
        return DescribeServers(raw, null);
    }

    public async Task<InstanceServerList> SaveServersAsync(string expectedRevision, IReadOnlyList<InstanceServerEdit> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > limits.MaxServers) throw new InvalidDataException("Server entry budget exceeded.");
        await mutation.WaitAsync(cancellationToken);
        try
        {
            var path = Child("servers.dat"); var raw = await ReadOptionalAsync(path, nbtLimits.MaxInputBytes, cancellationToken);
            if (!string.Equals(expectedRevision, Revision(raw), StringComparison.Ordinal)) throw new IOException("servers.dat changed; reload before saving.");
            var document = raw is null ? new NbtDocument(new(NbtType.Compound, "", Array.Empty<NbtTag>())) : BoundedNbt.Read(raw, nbtLimits, cancellationToken);
            var existing = ServerTags(document); var selected = new HashSet<int>(); var edited = new List<NbtTag>();
            foreach (var edit in entries)
            {
                ValidateServer(edit); IReadOnlyList<NbtTag> original = [];
                if (edit.ExistingIndex is { } index)
                {
                    if (index < 0 || index >= existing.Count || !selected.Add(index)) throw new InvalidDataException("Server row index is stale or repeated.");
                    original = existing[index].Children;
                    // Validate the fields being replaced before changing a document containing unknown tags.
                    _ = ReadString(existing[index].Find("name")); _ = ReadString(existing[index].Find("ip"));
                }
                var fields = original.ToList(); Replace(fields, new(NbtType.String, "name", edit.Name.Trim())); Replace(fields, new(NbtType.String, "ip", edit.Address.Trim()));
                edited.Add(new(NbtType.Compound, "", fields));
            }
            var root = document.Root.Children.ToList(); Replace(root, new(NbtType.List, "servers", new NbtList(NbtType.Compound, edited)));
            var output = BoundedNbt.Write(document with { Root = document.Root with { Value = root } }, nbtLimits, cancellationToken);
            var backup = await AtomicWriteAsync(path, output, raw, cancellationToken);
            return DescribeServers(output, backup);
        }
        finally { mutation.Release(); }
    }

    public async Task<IReadOnlyList<InstanceWorldSummary>> GetWorldsAsync(CancellationToken cancellationToken = default)
    {
        var saves = Child("saves"); var guard = Capture(saves, true);
        if (!guard.Exists) return [];
        if (!guard.Final.Directory) throw new IOException("saves must be a physical directory.");
        var directories = Directory.EnumerateDirectories(saves).Take(limits.MaxWorlds + 1).Order(StringComparer.Ordinal).ToArray(); guard.Verify();
        if (directories.Length > limits.MaxWorlds) throw new InvalidDataException("World directory budget exceeded.");
        var result = new List<InstanceWorldSummary>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested(); var name = Path.GetFileName(directory);
            try { result.Add(await ReadWorldAsync(name, cancellationToken)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { result.Add(new(name, name, null, null, null, null, null, null, "无法读取此世界的 level.dat（" + ex.GetType().Name + "）；原文件保持不变。")); }
        }
        guard.Verify(); return result;
    }

    public async Task<IReadOnlyList<WorldDatapackFile>> GetDatapacksAsync(string worldDirectoryName, CancellationToken cancellationToken = default)
    {
        _ = await ReadWorldAsync(worldDirectoryName, cancellationToken);
        var directory = WorldChild(worldDirectoryName, "datapacks"); var guard = Capture(directory, true);
        if (!guard.Exists) return [];
        if (!guard.Final.Directory) throw new IOException("datapacks must be a physical directory.");
        var paths = Directory.EnumerateFileSystemEntries(directory).Take(limits.MaxDatapacks + 1).Order(StringComparer.Ordinal).ToArray(); guard.Verify();
        if (paths.Length > limits.MaxDatapacks) throw new InvalidDataException("Datapack file budget exceeded.");
        var packs = new List<WorldDatapackFile>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested(); var item = Capture(path);
            if (item.Final.Directory || Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)) packs.Add(DescribePack(path, item.Final));
            item.Verify();
        }
        guard.Verify(); return packs;
    }

    public async Task<WorldDatapackFile> ImportDatapackAsync(string worldDirectoryName, string zipPath, CancellationToken cancellationToken = default)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            _ = await ReadWorldAsync(worldDirectoryName, cancellationToken);
            if (!Path.IsPathFullyQualified(zipPath) || !Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a local ZIP datapack file.");
            zipPath = Path.GetFullPath(zipPath); var leaf = Leaf(Path.GetFileName(zipPath));
            var source = WorldPathSnapshot.Capture(Path.GetDirectoryName(zipPath)!, zipPath);
            var bytes = await ReadGuardedAsync(source, limits.MaxPackBytes, cancellationToken);
            await ValidateDatapackAsync(bytes, cancellationToken);
            var directory = WorldChild(worldDirectoryName, "datapacks"); var parent = Capture(directory, true);
            if (!parent.Exists)
            {
                parent.Verify(); Directory.CreateDirectory(directory); parent.VerifyExistingNodes(); VerifyRoots();
                var created = Capture(directory); if (!created.Final.Directory) throw new IOException("Datapack destination is not a directory.");
            }
            else if (!parent.Final.Directory) throw new IOException("Datapack destination is not a directory.");
            var destination = SafePath.Within(directory, leaf);
            _ = await AtomicWriteAsync(destination, bytes, null, cancellationToken);
            var saved = Capture(destination); var result = DescribePack(destination, saved.Final); saved.Verify(); return result;
        }
        finally { mutation.Release(); }
    }

    private async Task<InstanceWorldSummary> ReadWorldAsync(string directoryName, CancellationToken ct)
    {
        var bytes = await ReadOptionalAsync(WorldChild(directoryName, "level.dat"), nbtLimits.MaxInputBytes, ct) ?? throw new InvalidDataException("World level.dat is missing.");
        var document = BoundedNbt.Read(bytes, nbtLimits, ct); var data = document.Root.Find("Data");
        if (data is not { Type: NbtType.Compound }) throw new InvalidDataException("Java world Data compound is missing.");
        var generation = data.Find("WorldGenSettings"); if (generation is not null && generation.Type != NbtType.Compound) throw new InvalidDataException("Invalid world generation compound.");
        var version = data.Find("Version"); if (version is not null && version.Type != NbtType.Compound) throw new InvalidDataException("Invalid world version compound.");
        var lastPlayed = ReadLong(data.Find("LastPlayed")); DateTimeOffset? played = null;
        if (lastPlayed is { } timestamp)
        {
            try { played = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("World LastPlayed timestamp is outside supported bounds."); }
        }
        var hardcore = data.Find("hardcore"); if (hardcore is not null && hardcore.Type != NbtType.Byte) throw new InvalidDataException("Invalid hardcore tag type.");
        return new(directoryName, ReadString(data.Find("LevelName")) ?? directoryName,
            ReadLong(generation?.Find("seed")) ?? ReadLong(data.Find("RandomSeed")), ReadString(version?.Find("Name")),
            ReadInt(data.Find("DataVersion")), ReadInt(data.Find("GameType")), hardcore is null ? null : (sbyte)hardcore.Value != 0, played);
    }
    private InstanceServerList DescribeServers(byte[]? bytes, string? backup)
    {
        if (bytes is null) return new("missing", [], backup);
        var document = BoundedNbt.Read(bytes, nbtLimits); var tags = ServerTags(document);
        return new(Revision(bytes), tags.Select((tag, index) => new InstanceServerEntry(index,
            ReadString(tag.Find("name")) ?? "", ReadString(tag.Find("ip")) ?? "")).ToArray(), backup);
    }
    private IReadOnlyList<NbtTag> ServerTags(NbtDocument document)
    {
        var servers = document.Root.Find("servers"); if (servers is null) return [];
        if (servers.Type != NbtType.List || servers.Value is not NbtList list || list.ElementType != NbtType.Compound && list.Items.Count != 0
            || list.Items.Count > limits.MaxServers) throw new InvalidDataException("Unsupported servers.dat list.");
        return list.Items;
    }
    private static string? ReadString(NbtTag? tag) => tag is null ? null : tag.Type == NbtType.String ? (string)tag.Value : throw new InvalidDataException("NBT string field has another type.");
    private static long? ReadLong(NbtTag? tag) => tag is null ? null : tag.Type == NbtType.Long ? (long)tag.Value : throw new InvalidDataException("NBT long field has another type.");
    private static int? ReadInt(NbtTag? tag) => tag is null ? null : tag.Type == NbtType.Int ? (int)tag.Value : throw new InvalidDataException("NBT int field has another type.");
    private static void Replace(List<NbtTag> fields, NbtTag value)
    {
        var matches = fields.Select((tag, i) => (tag, i)).Where(p => p.tag.Name == value.Name).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Ambiguous duplicate NBT field.");
        if (matches.Length == 1) fields[matches[0].i] = value; else fields.Add(value);
    }
    private static void ValidateServer(InstanceServerEdit entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 128 || entry.Name.Any(char.IsControl)) throw new InvalidDataException("Server name must have 1–128 non-control characters.");
        if (string.IsNullOrWhiteSpace(entry.Address) || entry.Address.Length > 1024 || entry.Address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or '\\' or '@' or '?' or '#'))
            throw new InvalidDataException("Server address must be a hostname or IP with an optional port.");
    }
    private static string Revision(byte[]? bytes) => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Leaf(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Length > 240 || value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || value.EndsWith('.') || value.EndsWith(' ')) throw new IOException("Unsafe world or datapack filename.");
        return value;
    }
    private string Child(string relative)
    {
        var safe = SafePath.Within(gameRoot, relative); var literal = Path.GetFullPath(Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(safe, literal, PathComparison)) throw new IOException("World paths must not traverse symbolic links.");
        return literal;
    }
    private string WorldChild(string world, string relative) => Child("saves/" + Leaf(world) + "/" + relative);
    private void VerifyRoots()
    {
        if (!WorldFileIdentity.AtPath(dataRoot).SameNode(dataIdentity) || !WorldFileIdentity.AtPath(gameRoot).SameNode(gameIdentity)) throw new IOException("Isolated instance path identity changed.");
    }
    private WorldPathSnapshot Capture(string path, bool allowMissing = false)
    {
        VerifyRoots(); var guard = WorldPathSnapshot.Capture(dataRoot, path, allowMissing); VerifyRoots(); return guard;
    }
    private async Task<byte[]?> ReadOptionalAsync(string path, int maximum, CancellationToken ct)
    {
        var guard = Capture(path, true); if (!guard.Exists) return null;
        var bytes = await ReadGuardedAsync(guard, maximum, ct); VerifyRoots(); return bytes;
    }
    private static async Task<byte[]> ReadGuardedAsync(WorldPathSnapshot guard, int maximum, CancellationToken ct)
    {
        guard.Verify(); if (!guard.Exists || guard.Final.Directory) throw new IOException("A regular file is required.");
        if (guard.Final.Size > maximum) throw new InvalidDataException("File exceeds its input byte budget.");
        await using var stream = new FileStream(guard.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        guard.Verify(); var identity = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
        if (identity != guard.Final) throw new IOException("File changed before read.");
        var length = checked((int)stream.Length); if (length < 0 || length > maximum) throw new InvalidDataException("File input byte budget exceeded.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        var extra = new byte[1]; if (await stream.ReadAsync(extra, ct) != 0 || WorldFileIdentity.FromHandle(stream.SafeFileHandle) != identity) throw new IOException("File changed during read.");
        guard.Verify(); return bytes;
    }

    private async Task<string?> AtomicWriteAsync(string destination, byte[] bytes, byte[]? original, CancellationToken ct)
    {
        var before = Capture(destination, true);
        if (before.Exists != (original is not null)) throw new IOException("Destination appeared or disappeared; reload before saving.");
        if (before.Exists && before.Final.Directory) throw new IOException("Destination is a directory.");
        if (before.Exists && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only world files are not replaced.");
        UnixFileMode? permissions = null;
        if (!OperatingSystem.IsWindows()) permissions = before.Exists ? File.GetUnixFileMode(destination) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var directory = Path.GetDirectoryName(destination)!; var parent = Capture(directory);
        var stage = SafePath.Within(directory, ".shou-world-" + Guid.NewGuid().ToString("N") + ".tmp");
        WorldFileIdentity? stageIdentity = null; string? backup = null;
        try
        {
            stageIdentity = await WriteNewAsync(stage, bytes, parent, ct, permissions);
            before.Verify(); parent.Verify();
            if (original is not null)
            {
                var current = await ReadOptionalAsync(destination, nbtLimits.MaxInputBytes, ct);
                if (Revision(current) != Revision(original)) throw new IOException("Destination changed while preparing the save.");
                backup = SafePath.Within(directory, Path.GetFileName(destination) + ".shou-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                await WriteNewAsync(backup, original, parent, ct, permissions);
            }
            ct.ThrowIfCancellationRequested(); before.Verify(); parent.Verify();
            if (Capture(stage).Final != stageIdentity.Value) throw new IOException("Staging file identity changed.");
            File.Move(stage, destination, original is not null);
            parent.Verify(); VerifyRoots();
            var after = Capture(destination);
            if (!after.Final.SameNode(stageIdentity.Value)) throw new IOException("Saved file identity differs from the validated staging file.");
            var written = await ReadOptionalAsync(destination, Math.Max(nbtLimits.MaxInputBytes, limits.MaxPackBytes), ct);
            if (Revision(written) != Revision(bytes)) throw new IOException("Saved content changed during commit verification.");
            return backup;
        }
        finally
        {
            if (stageIdentity is { } allocated)
            {
                try { parent.Verify(); var remainder = Capture(stage, true); if (remainder.Exists && remainder.Final.SameNode(allocated)) File.Delete(stage); }
                catch (IOException) { /* A changed path is left untouched for explicit recovery. */ }
            }
        }
    }
    private async Task<WorldFileIdentity> WriteNewAsync(string path, byte[] bytes, WorldPathSnapshot parent, CancellationToken ct, UnixFileMode? permissions)
    {
        parent.Verify(); var missing = Capture(path, true); if (missing.Exists) throw new IOException("New output already exists.");
        WorldFileIdentity? allocated = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = permissions ?? UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(path, options);
            allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
            parent.Verify(); var created = Capture(path); var opened = allocated.Value;
            if (!created.Final.SameNode(opened)) throw new IOException("New output path identity changed before write.");
            if (!OperatingSystem.IsWindows() && permissions is { } mode) File.SetUnixFileMode(stream.SafeFileHandle, mode);
            await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
            parent.Verify(); var finished = Capture(path); var completed = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
            if (finished.Final != completed || !completed.SameNode(opened) || completed.Size != bytes.Length) throw new IOException("New output path changed while writing.");
            return completed;
        }
        catch
        {
            if (allocated is { } identity)
            {
                try { parent.Verify(); var current = Capture(path, true); if (current.Exists && current.Final.SameNode(identity)) File.Delete(path); }
                catch (IOException) { /* A changed path is left untouched. */ }
            }
            throw;
        }
    }

    private async Task ValidateDatapackAsync(byte[] bytes, CancellationToken ct)
    {
        ValidateZipDirectoryBudget(bytes);
        using var memory = new MemoryStream(bytes, false); using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > limits.MaxPackEntries) throw new InvalidDataException("Datapack ZIP entry budget exceeded.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; var metadataFound = false; var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested(); var name = entry.FullName;
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl)
                || name.Split('/').Any(p => p is "." or "..") || !names.Add(name.TrimEnd('/'))) throw new InvalidDataException("Unsafe or duplicate datapack ZIP path.");
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type != 0 && type != 0x8000 && type != 0x4000) throw new InvalidDataException("Datapack ZIP links and special files are unsupported.");
            if (entry.Length < 0 || entry.Length > limits.MaxPackEntryBytes || entry.CompressedLength < 0
                || entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > limits.MaxCompressionRatio)
                || expanded + entry.Length > limits.MaxPackExpandedBytes) throw new InvalidDataException("Datapack ZIP expansion budget exceeded.");
            if (name.EndsWith('/')) { if (entry.Length != 0 || entry.Crc32 != 0) throw new InvalidDataException("ZIP directory contains file data."); continue; }
            using var data = entry.Open(); using var metadata = name == "pack.mcmeta" ? new MemoryStream() : null;
            long count = 0; uint crc = 0;
            while (true)
            {
                var read = await data.ReadAsync(buffer, ct); if (read == 0) break;
                count += read; expanded += read;
                if (count > entry.Length || count > limits.MaxPackEntryBytes || expanded > limits.MaxPackExpandedBytes || metadata is not null && count > 65536)
                    throw new InvalidDataException("Datapack ZIP stream exceeds its declared or configured budget.");
                crc = BoundedNbt.Crc32(buffer.AsSpan(0, read), crc); metadata?.Write(buffer, 0, read);
            }
            if (count != entry.Length || crc != entry.Crc32) throw new InvalidDataException("Datapack ZIP length or checksum is invalid.");
            if (metadata is not null)
            {
                using var json = JsonDocument.Parse(metadata.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Datapack pack.mcmeta needs a pack object.");
                metadataFound = true;
            }
        }
        if (!metadataFound) throw new InvalidDataException("Datapack ZIP must contain a root pack.mcmeta.");
    }
    private void ValidateZipDirectoryBudget(byte[] bytes)
    {
        // PKWARE APPNOTE 6.3.10 sections 4.3.12/4.3.16. Bound records before ZipArchive allocates its entry graph.
        // https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT
        var end = -1;
        for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x06054b50
                && (long)i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 20)) == bytes.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException("ZIP end record is missing or has trailing data.");
        var header = bytes.AsSpan(end);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]); var start = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != entries || entries == 65535 || size == uint.MaxValue || start == uint.MaxValue
            || entries == 0 || entries > limits.MaxPackEntries || (long)start + size != end)
            throw new InvalidDataException("ZIP directory is over budget or requires unsupported split/ZIP64 records.");
        var position = (int)start; var count = 0; long expanded = 0;
        while (position < end)
        {
            if (end - position < 46 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position)) != 0x02014b50 || ++count > limits.MaxPackEntries)
                throw new InvalidDataException("Invalid or over-budget ZIP central directory.");
            var record = bytes.AsSpan(position, end - position);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]); var method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]); var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            var name = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]); var extra = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]); var comment = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[34..]); var local = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);
            var length = 46 + name + extra + comment;
            if (length > end - position || name is 0 or > 4096 || disk != 0 || local >= start || (flags & 0x41) != 0 || method is not (0 or 8)
                || compressed == uint.MaxValue || uncompressed > limits.MaxPackEntryBytes || compressed > limits.MaxPackBytes
                || uncompressed > 0 && (compressed == 0 || uncompressed / (double)compressed > limits.MaxCompressionRatio)
                || (expanded += uncompressed) > limits.MaxPackExpandedBytes)
                throw new InvalidDataException("ZIP entry metadata exceeds the supported path/compression/size budget.");
            position += length;
        }
        if (count != entries || position != end) throw new InvalidDataException("ZIP central entry count differs from its end record.");
    }
    private static WorldDatapackFile DescribePack(string path, WorldFileIdentity identity) => new(Path.GetFileName(path), identity.Directory,
        identity.Directory ? null : identity.Size, new DateTimeOffset(identity.Directory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
}

internal sealed class WorldPathSnapshot
{
    private readonly string root;
    private readonly IReadOnlyList<(string Path, WorldFileIdentity Identity)> nodes;
    public string Path { get; }
    public bool Exists { get; }
    public WorldFileIdentity Final => Exists ? nodes[^1].Identity : throw new IOException("Path does not exist.");
    private WorldPathSnapshot(string root, string path, bool exists, IReadOnlyList<(string, WorldFileIdentity)> nodes) { this.root = root; Path = path; Exists = exists; this.nodes = nodes; }
    public static WorldPathSnapshot Capture(string root, string path, bool allowMissing = false)
    {
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)); path = System.IO.Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!SafePath.IsWithin(root, path) || !string.Equals(SafePath.Canonical(root), root, comparison) || !string.Equals(SafePath.Canonical(path), path, comparison)) throw new IOException("World path contains a link or escapes its physical boundary.");
        var relative = System.IO.Path.GetRelativePath(root, path); var parts = relative == "." ? Array.Empty<string>() : relative.Split(System.IO.Path.DirectorySeparatorChar);
        var result = new List<(string, WorldFileIdentity)> { (root, WorldFileIdentity.AtPath(root)) }; var current = root;
        foreach (var part in parts)
        {
            if (!result[^1].Item2.Directory) throw new IOException("World path parent is not a directory.");
            current = System.IO.Path.Combine(current, part); var info = new FileInfo(current); info.Refresh();
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("World file links are unsupported.");
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (allowMissing) return new(root, path, false, result);
                throw new FileNotFoundException("World path does not exist.");
            }
            result.Add((current, WorldFileIdentity.AtPath(current)));
        }
        return new(root, path, true, result);
    }
    public void Verify()
    {
        var fresh = Capture(root, Path, !Exists);
        if (fresh.Exists != Exists || fresh.nodes.Count != nodes.Count) throw new IOException("World path appeared or disappeared.");
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i].Identity.Directory ? !nodes[i].Identity.SameNode(fresh.nodes[i].Identity) : nodes[i].Identity != fresh.nodes[i].Identity)
                throw new IOException("World path identity changed.");
    }
    public void VerifyExistingNodes()
    {
        foreach (var node in nodes)
        {
            var current = WorldFileIdentity.AtPath(node.Path);
            if (node.Identity.Directory ? !node.Identity.SameNode(current) : node.Identity != current) throw new IOException("World parent path identity changed.");
        }
    }
}

// Native identity is needed because SafePath and JavaTrustTree's regular-file check do not return handle/path identity.
// Layouts: Apple XNU bsd/sys/stat.h __DARWIN_STRUCT_STAT64 (arm64); Linux include/uapi/linux/stat.h;
// Microsoft FILE_ID_INFO / BY_HANDLE_FILE_INFORMATION. Windows and Linux need their own native execution gates.
internal readonly record struct WorldFileIdentity(ulong Device, ulong Index, ulong IndexHigh, bool Directory, uint Links, long Size,
    long WriteSeconds, long WriteNanoseconds, long ChangeSeconds, long ChangeNanoseconds)
{
    public bool SameNode(WorldFileIdentity other) => Device == other.Device && Index == other.Index && IndexHigh == other.IndexHigh && Directory == other.Directory && (Directory || Links == other.Links);
    public static WorldFileIdentity AtPath(string path)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("World identity currently requires little-endian native hosts.");
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Cannot obtain world path identity."); return FromHandle(handle);
        }
        var bytes = new byte[256];
        if (OperatingSystem.IsMacOS()) { if (LStat(path, bytes) != 0) throw new IOException("Cannot stat world path."); return Darwin(bytes); }
        if (OperatingSystem.IsLinux()) { if (Statx(-100, path, 0x100, 0x7ff, bytes) != 0) throw new IOException("Cannot stat world path."); return Linux(bytes); }
        throw new PlatformNotSupportedException("World file identity is unavailable on this platform.");
    }
    public static WorldFileIdentity FromHandle(SafeFileHandle handle)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("World identity currently requires little-endian native hosts.");
        if (OperatingSystem.IsWindows())
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0) throw new IOException("World files must be regular, physical files.");
            var directory = (info.Attributes & 0x10) != 0;
            if (!directory && info.NumberOfLinks != 1) throw new IOException("Hard-linked world files are unsupported.");
            var id = new byte[24];
            if (!GetFileInformationByHandleEx(handle, 18, id, 24)) throw new IOException("Windows filesystem does not expose a stable 128-bit world file identity.");
            return new(BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt64LittleEndian(id.AsSpan(8)), BinaryPrimitives.ReadUInt64LittleEndian(id.AsSpan(16)), directory, info.NumberOfLinks,
                directory ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow,
                directory ? 0 : ((long)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime, 0, 0, 0);
        }
        var bytes = new byte[256]; var fd = handle.DangerousGetHandle().ToInt32();
        if (OperatingSystem.IsMacOS()) { if (FStat(fd, bytes) != 0) throw new IOException("Cannot stat open world file."); return Darwin(bytes); }
        if (OperatingSystem.IsLinux()) { if (Statx(fd, "", 0x1000, 0x7ff, bytes) != 0) throw new IOException("Cannot stat open world file."); return Linux(bytes); }
        throw new PlatformNotSupportedException("World file identity is unavailable on this platform.");
    }
    private static WorldFileIdentity Darwin(byte[] bytes)
    {
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)); var links = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        var directory = ValidateType(mode, links);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)), 0, directory, links,
            directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(96)), directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)),
            directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56)), directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64)), directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72)));
    }
    private static WorldFileIdentity Linux(byte[] bytes)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x3c7) != 0x3c7) throw new IOException("Filesystem does not expose required statx identity fields.");
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)); var links = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        var directory = ValidateType(mode, links);
        return new(((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)), 0, directory, links,
            directory ? 0 : checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40))), directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(112)),
            directory ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(120)), directory ? 0 : BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(96)), directory ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104)));
    }
    private static bool ValidateType(uint mode, uint links)
    {
        var type = mode & 0xf000; if (type != 0x4000 && type != 0x8000 || type == 0x8000 && links != 1) throw new IOException("World links and special files are unsupported.");
        return type == 0x4000;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowsInformation
    {
        internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(int descriptor, [Out] byte[] buffer);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);
}
