using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Reads display metadata only; never executes or extracts a mod.</summary>
internal static partial class ModArchiveMetadata
{
    internal sealed record Metadata(string? Name, string? Version, byte[]? Icon);
    internal static Metadata Read(string path, bool includeIcon)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 512L * 1024 * 1024) return new(null, null, null);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > 40000) return new(null, null, null);
            string? name = null, version = null, icon = null;
            if (zip.GetEntry("fabric.mod.json") is { } fabric)
            {
                var root = JsonNode.Parse(Read(fabric, 128 * 1024));
                name = (string?)root?["name"]; version = (string?)root?["version"];
                var value = root?["icon"];
                icon = value is JsonValue ? (string?)value : (value as JsonObject)?.OrderBy(p => int.TryParse(p.Key, out var n) && n is > 0 and <= 2048 ? Math.Abs(n - 64) : int.MaxValue).FirstOrDefault().Value?.GetValue<string>();
            }
            else if (zip.GetEntry("quilt.mod.json") is { } quilt)
            {
                var root = JsonNode.Parse(Read(quilt, 128 * 1024))?["quilt_loader"];
                name = (string?)root?["metadata"]?["name"]; version = (string?)root?["version"]; icon = (string?)root?["metadata"]?["icon"];
            }
            else if ((zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml")) is { } forge)
            {
                var text = System.Text.Encoding.UTF8.GetString(Read(forge, 128 * 1024));
                name = Field(text, "displayName"); version = Field(text, "version"); icon = Field(text, "logoFile");
            }
            else if (zip.GetEntry("mcmod.info") is { } legacy)
            {
                var root = JsonNode.Parse(Read(legacy, 128 * 1024));
                var mod = root is JsonArray array ? array.FirstOrDefault() : (root?["modList"] as JsonArray)?.FirstOrDefault();
                name = (string?)mod?["name"]; version = (string?)mod?["version"]; icon = (string?)mod?["logoFile"];
            }
            byte[]? png = null;
            if (includeIcon && icon is { Length: > 0 and <= 256 } && !icon.StartsWith('/') && !icon.Contains('\\') && !icon.Split('/').Any(p => p is ".." or "." or "") && zip.GetEntry(icon) is { } entry)
            {
                var bytes = Read(entry, 256 * 1024);
                if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) is > 0 and <= 2048
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) is > 0 and <= 2048) png = bytes;
            }
            return new(Label(name), Label(version)?.Contains("${", StringComparison.Ordinal) == true ? null : Label(version), png);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { return new(null, null, null); }
    }
    private static string? Label(string? value) => value is { Length: > 0 and <= 160 } && !value.Any(char.IsControl) ? value : null;
    private static string? Field(string text, string key) => Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + "\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Groups[1].Value;
    private static byte[] Read(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException();
        using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var n = input.Read(buffer); if (n == 0) break; if (output.Length + n > limit) throw new InvalidDataException(); output.Write(buffer, 0, n); }
        if (output.Length != entry.Length) throw new InvalidDataException();
        return output.ToArray();
    }
}
