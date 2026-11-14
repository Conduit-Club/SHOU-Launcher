using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SHOU.Launcher.Core.Instances;

/// <summary>Reads display metadata only; never executes or extracts a mod.</summary>
internal static partial class ModArchiveMetadata
{
    internal sealed record Metadata(string? Name, string? Version, byte[]? Icon, string? Id = null,
        string? Description = null, IReadOnlyList<string>? Dependencies = null, IReadOnlyList<string>? Embedded = null, string? Error = null);
    internal static Metadata Read(string path, bool includeIcon)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 512L * 1024 * 1024) return new(null, null, null);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > 40000) return new(null, null, null);
            string? name = null, version = null, icon = null, id = null, description = null;
            var dependencies = new List<string>(); var embedded = new List<string>();
            if (zip.GetEntry("fabric.mod.json") is { } fabric)
            {
                var root = LocalDisplayJson.Parse(Read(fabric, 128 * 1024));
                name = (string?)root?["name"]; version = (string?)root?["version"];
                id = (string?)root?["id"]; description = (string?)root?["description"];
                dependencies.AddRange((root?["depends"] as JsonObject ?? []).Select(p => p.Key));
                foreach (var item in root?["jars"] as JsonArray ?? []) if ((string?)item?["file"] is { } nested) embedded.Add(NestedName(zip, nested));
                var value = root?["icon"];
                icon = value is JsonValue ? (string?)value : (value as JsonObject)?.OrderBy(p => int.TryParse(p.Key, out var n) && n is > 0 and <= 2048 ? Math.Abs(n - 64) : int.MaxValue).FirstOrDefault().Value?.GetValue<string>();
            }
            else if (zip.GetEntry("quilt.mod.json") is { } quilt)
            {
                var root = LocalDisplayJson.Parse(Read(quilt, 128 * 1024))?["quilt_loader"];
                name = (string?)root?["metadata"]?["name"]; version = (string?)root?["version"]; icon = (string?)root?["metadata"]?["icon"];
                id = (string?)root?["id"]; description = (string?)root?["metadata"]?["description"];
                foreach (var dep in root?["depends"] as JsonArray ?? []) if (dep is JsonValue value) dependencies.Add(value.GetValue<string>()); else if ((string?)dep?["id"] is { } key && (bool?)dep?["optional"] != true) dependencies.Add(key);
            }
            else if ((zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml")) is { } forge)
            {
                var text = System.Text.Encoding.UTF8.GetString(Read(forge, 128 * 1024));
                name = Field(text, "displayName"); version = Field(text, "version"); icon = Field(text, "logoFile");
                id = Field(text, "modId"); description = Field(text, "description");
                foreach (var block in Regex.Split(text, @"(?m)^\s*\[\[dependencies\.[^\]]+\]\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)).Skip(1))
                    if (Regex.IsMatch(block, @"(?m)^\s*mandatory\s*=\s*true", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) || Field(block, "type") == "required")
                        if (Field(block, "modId") is { Length: > 0 } key) dependencies.Add(key);
            }
            else if (zip.GetEntry("mcmod.info") is { } legacy)
            {
                var root = LocalDisplayJson.Parse(Read(legacy, 128 * 1024));
                var mod = root is JsonArray array ? array.FirstOrDefault() : (root?["modList"] as JsonArray)?.FirstOrDefault();
                name = (string?)mod?["name"]; version = (string?)mod?["version"]; icon = (string?)mod?["logoFile"];
                id = (string?)mod?["modid"]; description = (string?)mod?["description"];
                if (mod?["requiredMods"] is JsonArray required) dependencies.AddRange(required.Select(d => (string?)d ?? "").Select(d => d.Split('@')[0]));
            }
            if (zip.GetEntry("META-INF/jarjar/metadata.json") is { } jarjar)
                foreach (var item in LocalDisplayJson.Parse(Read(jarjar, 128 * 1024))?["jars"] as JsonArray ?? [])
                    if ((string?)item?["path"] is { } nested) embedded.Add(NestedName(zip, nested));
            byte[]? png = null;
            if (includeIcon && icon is { Length: > 0 and <= 256 } && !icon.StartsWith('/') && !icon.Contains('\\') && !icon.Split('/').Any(p => p is ".." or "." or "") && zip.GetEntry(icon) is { } entry)
            {
                var bytes = Read(entry, 256 * 1024);
                if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) is > 0 and <= 2048
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) is > 0 and <= 2048) png = bytes;
            }
            return new(Label(name), Label(version)?.Contains("${", StringComparison.Ordinal) == true ? null : Label(version), png,
                Label(id), LocalDisplayJson.Description(description), dependencies.Where(d => Label(d) is not null).Distinct(StringComparer.Ordinal).ToArray(), embedded.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { return new(null, null, null, Error: "无法读取模组归档或元数据。"); }
    }
    private static string NestedName(ZipArchive archive, string path)
    {
        if (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is ".." or "." or "")) throw new InvalidDataException();
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException();
        if (entry.Length is <= 0 or > 16 * 1024 * 1024) return Path.GetFileName(path);
        using var bytes = new MemoryStream(Read(entry, 16 * 1024 * 1024)); using var nested = new ZipArchive(bytes, ZipArchiveMode.Read);
        var metadata = nested.GetEntry("fabric.mod.json") ?? nested.GetEntry("quilt.mod.json");
        if (metadata is null) return Path.GetFileName(path);
        var json = LocalDisplayJson.Parse(Read(metadata, 128 * 1024)); var root = json?["quilt_loader"] ?? json;
        var name = Label((string?)root?["name"] ?? (string?)root?["id"]); var version = Label((string?)root?["version"]);
        return name is null ? Path.GetFileName(path) : name + (version is null ? "" : " " + version);
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
