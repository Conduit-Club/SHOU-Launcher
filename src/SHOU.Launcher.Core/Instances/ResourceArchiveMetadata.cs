using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Local display metadata only. Does not extract, enable or execute a pack.</summary>
internal static class ResourceArchiveMetadata
{
    internal sealed record Metadata(string Name, string? Description = null, byte[]? Icon = null, string? Error = null);
    internal static Metadata Read(string path, bool includeIcon, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(path); string? description = null; byte[]? icon = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            ct.ThrowIfCancellationRequested();
            if (zip.Entries.Count > 40000) return new(name, Error: "资源归档条目过多，未读取展示信息。");
            // A malformed description must not suppress a valid local pack icon or file row.
            if (zip.GetEntry("pack.mcmeta") is { } metadata)
            {
                try { description = Flatten(LocalDisplayJson.Parse(Read(metadata, 128 * 1024, ct))?["pack"]?["description"]); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or InvalidDataException) { }
            }
            if (includeIcon && (zip.GetEntry("pack.png") ?? zip.GetEntry("icon.png")) is { } image)
            {
                try
                {
                    var png = Read(image, 256 * 1024, ct);
                    if (png.Length >= 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) is > 0 and <= 2048
                        && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) is > 0 and <= 2048) icon = png;
                }
                catch (InvalidDataException) { }
            }
            return new(name, description, icon);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        { return new(name, Error: "无法读取资源归档展示信息。"); }
    }
    private static string? Flatten(JsonNode? node)
    {
        var text = new System.Text.StringBuilder(); var nodes = 0;
        void Append(JsonNode? value, int depth)
        {
            if (value is null || depth > 16 || ++nodes > 512 || text.Length >= 2048) return;
            if (value is JsonValue atom && atom.TryGetValue<string>(out var str)) text.Append(str.AsSpan(0, Math.Min(str.Length, 2048 - text.Length)));
            else if (value is JsonArray array) { foreach (var item in array) Append(item, depth + 1); }
            else if (value is JsonObject obj) { Append(obj["text"], depth + 1); Append(obj["extra"], depth + 1); }
        }
        Append(node, 0);
        var result = string.Join(" ", text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result;
    }
    private static byte[] Read(ZipArchiveEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length < 0 || entry.Length > maximum) throw new InvalidDataException();
        using var input = entry.Open(); using var output = new MemoryStream(); var bytes = new byte[8192];
        while (true) { ct.ThrowIfCancellationRequested(); var n = input.Read(bytes); if (n == 0) break; if (output.Length + n > maximum) throw new InvalidDataException(); output.Write(bytes, 0, n); }
        if (output.Length != entry.Length) throw new InvalidDataException(); return output.ToArray();
    }
}
