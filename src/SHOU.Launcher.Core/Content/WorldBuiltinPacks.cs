using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Content;

internal static class WorldBuiltinPacks
{
    // This mapping is verified against Fabric's 1.21.1 ModResourcePackCreator.FABRIC.
    // Unknown versions and other mod-provided IDs still require their own identity mapping.
    internal static async Task<IReadOnlySet<string>> ResolveAsync(GameInstance instance, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (instance.Loader != "fabric" || (instance.GameVersionId ?? instance.VersionId) != "1.21.1") return result;
        var manifest = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
        foreach (var file in manifest.Files.Where(f => f.RelativePath.StartsWith("mods/", StringComparison.Ordinal) && f.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(file.RelativePath);
            if (!name.StartsWith("fabric-api-", StringComparison.Ordinal) && !name.StartsWith("fabric-resource-loader-v0-", StringComparison.Ordinal)) continue;
            var path = ContentInstallFiles.Literal(instance.GameDirectory, file.RelativePath);
            if (!await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, ct).ConfigureAwait(false)) continue;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                if (!Convert.ToHexStringLower(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false)).Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) continue;
            using var archive = PackArchiveReader.Open(path);
            var entry = archive.Zip.GetEntry("fabric.mod.json"); if (entry is null) continue;
            using var json = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(entry, 128 * 1024, ct).ConfigureAwait(false));
            var module = json.RootElement; var resourceLoader = Id(module) == "fabric-resource-loader-v0" && HasProvider(archive.Zip);
            if (Id(module) == "fabric-api" && module.TryGetProperty("jars", out var jars) && jars.ValueKind == JsonValueKind.Array && jars.GetArrayLength() <= 256)
            {
                foreach (var jar in jars.EnumerateArray())
                {
                    var nested = jar.GetProperty("file").GetString() ?? ""; ModrinthPackArchive.SafeRelative(nested);
                    if (!nested.StartsWith("META-INF/jars/fabric-resource-loader-v0-", StringComparison.Ordinal) || !nested.EndsWith(".jar", StringComparison.Ordinal)) continue;
                    var nestedEntry = archive.Zip.GetEntry(nested); if (nestedEntry is null) continue;
                    using var bytes = new MemoryStream(await PackArchiveReader.ReadEntryAsync(nestedEntry, 4 * 1024 * 1024, ct).ConfigureAwait(false));
                    PackArchiveReader.ValidateDirectory(bytes); bytes.Position = 0;
                    using var zip = new ZipArchive(bytes, ZipArchiveMode.Read); var metadata = zip.GetEntry("fabric.mod.json");
                    if (metadata is null || zip.Entries.Count > 2048 || zip.Entries.Count(e => e.FullName == "fabric.mod.json") != 1) continue;
                    using var description = PackArchiveReader.Json(await PackArchiveReader.ReadEntryAsync(metadata, 128 * 1024, ct).ConfigureAwait(false));
                    resourceLoader |= Id(description.RootElement) == "fabric-resource-loader-v0" && HasProvider(zip);
                }
            }
            archive.Verify();
            if (resourceLoader && await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, ct).ConfigureAwait(false)) { result.Add("fabric"); return result; }
        }
        return result;
    }
    private static string? Id(JsonElement module) => module.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    private static bool HasProvider(ZipArchive zip) => zip.GetEntry("net/fabricmc/fabric/impl/resource/loader/ModResourcePackCreator.class") is not null;
}
