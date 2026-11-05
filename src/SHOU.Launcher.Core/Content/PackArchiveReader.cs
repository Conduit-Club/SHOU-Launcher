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
    internal const long MaximumArchiveBytes = 256L * 1024 * 1024, MaximumExpandedBytes = 512L * 1024 * 1024, MaximumFileBytes = 128L * 1024 * 1024;
    internal const int MaximumEntries = 5000;
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
        if (guard.Final.Directory || guard.Final.Size is <= 0 or > MaximumArchiveBytes) throw new InvalidDataException("整合包 ZIP 须为不超过 256 MiB 的普通文件。");
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
        Verify(); input.Position = 0; await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
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
            if (entry.Length > MaximumFileBytes || (total = checked(total + entry.Length)) > MaximumExpandedBytes || entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 1000)) throw new InvalidDataException("ZIP 单项、解压总量或压缩比超过预算。");
            if (!directory) files.Add(name);
        }
        CheckTargets(files);
    }
    internal static void ValidateDirectory(FileStream input)
    {
        if (input.Length < 22) throw new InvalidDataException("ZIP 文件不完整。");
        var tail = new byte[(int)Math.Min(input.Length, 65557)]; input.Position = input.Length - tail.Length; input.ReadExactly(tail); var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--) if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException("ZIP 中央目录缺失。");
        var e = tail.AsSpan(end); var count = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]); var size = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]); var offset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        if (count is 0 or > MaximumEntries || size > 4 * 1024 * 1024 || offset + (long)size != input.Length - tail.Length + end || BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) != count) throw new InvalidDataException("不支持 ZIP64、多卷或超过 5000 项的 ZIP。");
        input.Position = offset; var header = new byte[46];
        for (var i = 0; i < count; i++)
        {
            input.ReadExactly(header); var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)); var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50 || (flags & 0x41) != 0 || method is not (0 or 8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) == uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == uint.MaxValue || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0) throw new InvalidDataException("ZIP 加密、压缩方法或条目目录无效。");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (length <= 0 || input.Position + length > offset + (long)size) throw new InvalidDataException("ZIP 目录长度无效。"); input.Position += length;
        }
        if (input.Position != offset + size) throw new InvalidDataException("ZIP 目录数量不一致。");
    }
    public void Dispose() { Zip.Dispose(); input.Dispose(); }
}
