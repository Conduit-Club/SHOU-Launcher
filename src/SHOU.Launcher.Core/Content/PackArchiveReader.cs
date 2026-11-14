using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Content;

/// <summary>Bound a ZIP before ZipArchive allocates entries; retain physical path/handle identity while inspecting it.</summary>
internal sealed class PackArchiveReader : IDisposable
{
    internal const long MaximumArchiveBytes = long.MaxValue, MaximumExpandedBytes = long.MaxValue, MaximumFileBytes = long.MaxValue;
    internal const int MaximumEntries = int.MaxValue;
    private readonly FileStream input;
    private readonly WorldPathSnapshot guard;
    private readonly WorldFileIdentity identity;
    internal ZipArchive Zip { get; }
    private PackArchiveReader(FileStream input, WorldPathSnapshot guard, WorldFileIdentity identity)
    { this.input = input; this.guard = guard; this.identity = identity; Zip = new(input, ZipArchiveMode.Read, true); }
    internal static PackArchiveReader Open(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择本机整合包的绝对路径。");
        path = Path.GetFullPath(path); var guard = WorldPathSnapshot.Capture(Path.GetDirectoryName(path)!, path);
        if (guard.Final.Directory || guard.Final.Size <= 0) throw new InvalidDataException("请选择普通整合包文件。");
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        try
        {
            var identity = WorldFileIdentity.FromHandle(input.SafeFileHandle); guard.Verify();
            if (identity != guard.Final || input.Length != identity.Size) throw new IOException("整合包文件身份已改变。");
            ValidateDirectory(input); input.Position = 0;
            var reader = new PackArchiveReader(input, guard, identity); reader.ValidateEntries(); reader.Verify(); return reader;
        }
        catch { input.Dispose(); throw; }
    }
    internal void Verify()
    { guard.Verify(); if (WorldFileIdentity.FromHandle(input.SafeFileHandle) != identity) throw new IOException("整合包在读取期间已改变。"); }
    internal async Task<string> Sha256Async(CancellationToken ct)
    {
        Verify(); input.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[131072]; long read = 0;
        while (true) { var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, identity.Size + 1 - read)), ct).ConfigureAwait(false); if (count == 0) break; read += count; if (read > identity.Size) throw new IOException("整合包长度在读取期间已改变。"); hash.AppendData(buffer, 0, count); }
        if (read != identity.Size) throw new IOException("整合包长度在读取期间已改变。"); Verify(); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal async Task CopyToAsync(string destination, CancellationToken ct)
    {
        Verify(); PackDiskSpace.Require(Path.GetDirectoryName(destination)!, identity.Size); input.Position = 0; await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        var buffer = new byte[131072]; long read = 0;
        while (true) { var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, identity.Size + 1 - read)), ct).ConfigureAwait(false); if (count == 0) break; read += count; if (read > identity.Size) throw new IOException("整合包长度在复制期间已改变。"); await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
        if (read != identity.Size) throw new IOException("整合包长度在复制期间已改变。"); await output.FlushAsync(ct).ConfigureAwait(false); Verify();
    }
    internal static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, long maximum, CancellationToken ct)
    {
        if (entry.Length < 0 || entry.Length > maximum) throw new InvalidDataException("整合包条目超过大小上限。");
        await using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[32768]; uint crc = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Length + 1 - output.Length)), ct).ConfigureAwait(false); if (count == 0) break;
            if (output.Length + count > entry.Length || output.Length + count > maximum) throw new InvalidDataException("整合包条目解压超过声明长度。");
            crc = BoundedNbt.Crc32(buffer.AsSpan(0, count), crc); output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length || crc != entry.Crc32) throw new InvalidDataException("整合包条目长度或 CRC 校验失败。"); return output.ToArray();
    }
    internal static JsonDocument Json(byte[] bytes)
    {
        try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); var result = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); Unique(result.RootElement); return result; }
        catch (DecoderFallbackException) { throw new InvalidDataException("整合包元数据不是有效 UTF-8。"); }
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var item in element.EnumerateObject()) { if (!seen.Add(item.Name)) throw new InvalidDataException("整合包元数据含重复 JSON 键。"); Unique(item.Value); } }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    internal static void CheckTargets(IEnumerable<string> names)
    {
        var keys = names.Select(ContentInstallFiles.PathKey).ToHashSet(StringComparer.Ordinal);
        foreach (var path in keys) { var slash = path.IndexOf('/'); while (slash >= 0) { if (keys.Contains(path[..slash])) throw new InvalidDataException("整合包文件与目录路径冲突。"); slash = path.IndexOf('/', slash + 1); } }
    }
    private void ValidateEntries()
    {
        long total = 0; var files = new List<string>(); var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Zip.Entries)
        {
            var directory = entry.FullName.EndsWith('/'); var name = directory ? entry.FullName[..^1] : entry.FullName; ModrinthPackArchive.SafeRelative(name);
            if (!paths.Add(ContentInstallFiles.PathKey(name))) throw new InvalidDataException("ZIP 含大小写、Unicode 或重复路径冲突。");
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type is not (0 or 0x8000 or 0x4000) || type == 0x4000 && !directory || directory && entry.Length != 0) throw new InvalidDataException("ZIP 链接、特殊文件或目录条目无效。");
            total = checked(total + entry.Length);
            if (!directory) files.Add(name);
        }
        CheckTargets(files);
    }
    internal static void ValidateDirectory(Stream input)
    {
        // ZipArchive validates standard ZIP and ZIP64 directories. No launcher-specific size/count gate.
        input.Position = 0; using var zip = new ZipArchive(input, ZipArchiveMode.Read, true); _ = zip.Entries.Count;
    }
    internal static async Task<(long Size, string Sha1, string Sha512)> StreamEntryAsync(ZipArchiveEntry entry, string? destination, CancellationToken ct)
    {
        if (entry.Length < 0) throw new InvalidDataException("整合包条目大小无效。");
        if (destination is not null) { PackDiskSpace.Require(Path.GetDirectoryName(destination)!, entry.Length); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); }
        await using var input = entry.Open();
        await using var output = destination is null ? Stream.Null : new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[131072]; long length = 0; uint crc = 0;
        while (true) { var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break; length = checked(length + count); if (length > entry.Length) throw new InvalidDataException("条目展开长度与清单不符。"); crc = BoundedNbt.Crc32(buffer.AsSpan(0, count), crc); sha1.AppendData(buffer, 0, count); sha512.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
        if (length != entry.Length || crc != entry.Crc32) throw new InvalidDataException("条目长度或 CRC 校验失败。");
        return (length, Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha512.GetHashAndReset()));
    }
    public void Dispose() { Zip.Dispose(); input.Dispose(); }
}
