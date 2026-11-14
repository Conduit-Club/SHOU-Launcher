using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private async Task<string> CacheImportedProfileAsync(LocalPackInspection inspection, string stage, CancellationToken ct)
    {
        var legacy = inspection.LegacyProfile;
        if (legacy is null) return await CacheLoaderProfileAsync(inspection.Loader, inspection.GameVersion, inspection.LoaderVersion, ct).ConfigureAwait(false);
        var baseId = legacy.CompleteVersionProfile || legacy.Components?.Any(c => c.Loader == inspection.Loader && c.Overridden) == true ? inspection.GameVersion : await CacheLoaderProfileAsync(inspection.Loader, inspection.GameVersion, inspection.LoaderVersion, ct).ConfigureAwait(false);
        JsonObject? overlay = null;
        foreach (var component in legacy.Components ?? [])
        {
            if (component.Overridden) continue;
            if (component.Loader == inspection.Loader && component.Version == inspection.LoaderVersion) continue;
            var id = await CacheLoaderProfileAsync(component.Loader, inspection.GameVersion, component.Version, ct).ConfigureAwait(false);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(SafePath.Within(Paths.CacheDirectory, $"versions/{id}/{id}.json"), ct))!.AsObject();
            json.Remove("inheritsFrom"); overlay = overlay is null ? json : VersionMetadataResolver.Merge(overlay, json);
        }
        if (legacy.VersionJson is { } text)
        {
            var imported = JsonNode.Parse(text)!.AsObject(); imported.Remove("inheritsFrom"); imported.Remove("jar");
            overlay = overlay is null ? imported : VersionMetadataResolver.Merge(overlay, imported);
        }
        if (overlay is null) return baseId;
        var profileId = "shou-import-" + inspection.ArchiveSha256[..24]; overlay["id"] = profileId; if (!legacy.CompleteVersionProfile) overlay["inheritsFrom"] = baseId;
        overlay["shouImportedGameVersion"] = inspection.GameVersion;
        if (legacy.CompleteVersionProfile || legacy.Components?.Any(c => c.Loader == inspection.Loader && c.Overridden) == true) overlay["shouImportedLoader"] = inspection.Loader;
        // Keep imported client/library files in this archive's cache namespace. Never overwrite shared version metadata.
        var sourceJar = legacy.ClientJarRelativePath is { } jarRelative ? ContentInstallFiles.Literal(stage, jarRelative) : null;
        if (sourceJar is not null)
        {
            var cacheJar = SafePath.Within(Paths.CacheDirectory, $"versions/{profileId}/{profileId}.jar"); Directory.CreateDirectory(Path.GetDirectoryName(cacheJar)!); SafePath.RejectLinks(sourceJar); SafePath.RejectLinks(cacheJar);
            await using var input = File.OpenRead(sourceJar); var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ct)); input.Position = 0;
            await using (var output = new FileStream(cacheJar + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 131072, true)) await input.CopyToAsync(output, ct);
            File.Move(cacheJar + ".tmp", cacheJar, true); overlay["jar"] = profileId;
            // A local, verified client can retain its package hash while the parent supplies other vanilla metadata.
            overlay["shouLocalClient"] = new JsonObject { ["sha1"] = sha1, ["size"] = input.Length };
        }
        foreach (var node in overlay["libraries"] as JsonArray ?? [])
        {
            if (node is not JsonObject library || (string?)library["name"] is not { } coordinate) continue;
            var downloads = library["downloads"] as JsonObject;
            var artifacts = new List<(JsonObject Json, string Path)>();
            if (downloads?["artifact"] is JsonObject artifact) artifacts.Add((artifact, (string?)artifact["path"] ?? LibraryResolver.MavenPath(coordinate)));
            else if (downloads is null && library["natives"] is null)
            {
                var maven = LibraryResolver.MavenPath(coordinate);
                if (File.Exists(ContentInstallFiles.Literal(stage, "libraries/" + maven)))
                { downloads = new(); library["downloads"] = downloads; var local = new JsonObject { ["url"] = ((string?)library["url"] ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + maven }; downloads["artifact"] = local; artifacts.Add((local, maven)); }
            }
            if (downloads?["classifiers"] is JsonObject classifiers)
                foreach (var pair in classifiers) if (pair.Value is JsonObject descriptor) artifacts.Add((descriptor, (string?)descriptor["path"] ?? LibraryResolver.MavenPath(coordinate + ":" + pair.Key)));
            foreach (var (descriptor, relative) in artifacts)
            {
                var source = ContentInstallFiles.Literal(stage, "libraries/" + relative); if (!File.Exists(source)) continue;
                var owned = profileId + "/" + relative; var target = ContentInstallFiles.Literal(Paths.CacheDirectory, "libraries/" + owned);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source); var hash = Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ct)); input.Position = 0;
                await using (var output = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 131072, true)) await input.CopyToAsync(output, ct);
                File.Move(target + ".tmp", target, true); descriptor["path"] = owned; descriptor["sha1"] = hash; descriptor["size"] = input.Length; descriptor["shouGenerated"] = true;
            }
        }
        var path = SafePath.Within(Paths.CacheDirectory, $"versions/{profileId}/{profileId}.json"); SafePath.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", overlay.ToJsonString(), ct); ct.ThrowIfCancellationRequested(); File.Move(path + ".tmp", path, true);
        return profileId;
    }
}
