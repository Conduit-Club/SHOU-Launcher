using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;
using PCL.Core.Conduit;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record ResolvedVersion(string Id, JsonObject Metadata, string MetadataPath, IReadOnlyList<string> InheritanceChain)
{
    public int RequiredJavaMajor => (int?)Metadata["javaVersion"]?["majorVersion"] ?? 8;
    public string JavaComponent => (string?)Metadata["javaVersion"]?["component"] ?? "jre-legacy";
}

public sealed class VersionMetadataResolver(HttpClient http, IVerifiedDownloader downloader, string cacheDirectory,
    Func<IReadOnlyList<DownloadSource>> sources)
{
    public async Task<ResolvedVersion> ResolveAsync(string id, CancellationToken ct = default)
    {
        var stack = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<string>();
        async Task<JsonObject> Visit(string versionId)
        {
            SafePath.Identifier(versionId);
            if (!stack.Add(versionId)) throw new InvalidDataException("Version inheritance cycle: " + string.Join(" -> ", stack.Append(versionId)));
            if (stack.Count > 24) throw new InvalidDataException("Version inheritance is too deep.");
            var metadata = await LoadAsync(versionId, ct).ConfigureAwait(false);
            if ((string?)metadata["inheritsFrom"] is { } parent)
                metadata = Merge(await Visit(parent).ConfigureAwait(false), metadata);
            stack.Remove(versionId);
            chain.Add(versionId);
            return metadata;
        }
        var merged = await Visit(id).ConfigureAwait(false);
        return new(id, merged, SafePath.Within(cacheDirectory, $"versions/{id}/{id}.json"), chain);
    }
    public async Task<JsonObject> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = SafePath.Within(cacheDirectory, $"versions/{SafePath.Identifier(id)}/{id}.json");
        if (File.Exists(path))
        {
            try { return ParseVersion(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), id); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { throw new InvalidDataException("Cached version metadata is corrupt; reinstall explicitly.", ex); }
        }
        var manifestSources = DownloadSources.Resolve([DownloadSources.ManifestUrl], sources());
        var errors = new List<Exception>();
        foreach (var source in manifestSources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var manifest = DownloadSources.ValidateManifest(await http.GetStringAsync(source.Url, timeout.Token).ConfigureAwait(false), id);
                var entry = manifest["versions"]!.AsArray().First(n => (string?)n?["id"] == id)!;
                var url = (string?)entry["url"] ?? throw new InvalidDataException("Version URL missing.");
                var hashes = (string?)entry["sha1"];
                var candidates = DownloadSources.Resolve([url], sources()).Select(a => new Uri(a.Url)).ToArray();
                // A curated mirror's rewritten metadata must use its own declared hash, never Mojang's guessed hash.
                var request = new DownloadRequest(candidates, path, Sha1: hashes, AllowResume: hashes is not null);
                if (hashes is null)
                {
                    // Small unhashed catalogs remain data-only; validate before installing the cache file.
                    var text = await http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
                    var parsed = ParseVersion(text, id);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path + ".tmp", text, ct).ConfigureAwait(false);
                    File.Move(path + ".tmp", path, true);
                    return parsed;
                }
                await downloader.DownloadAsync(request, cancellationToken: ct).ConfigureAwait(false);
                return ParseVersion(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or AggregateException or KeyNotFoundException or OperationCanceledException or System.Text.Json.JsonException or FormatException)
            { errors.Add(new IOException($"Metadata source {source.Name}: {ex.GetType().Name}")); }
        }
        throw new AggregateException($"No metadata source can provide {id}.", errors);
    }
    public static JsonObject ParseVersion(string json, string expectedId)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Version metadata is not an object.");
        if ((string?)node["id"] != expectedId) throw new InvalidDataException("Version metadata ID mismatch.");
        if (node["inheritsFrom"] is null && node["mainClass"] is null) throw new InvalidDataException("Version metadata has no main class or parent.");
        return node;
    }
    public static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var pair in child)
        {
            if (pair.Key == "inheritsFrom") continue;
            if (pair.Key == "libraries" && pair.Value is JsonArray libraries)
            {
                var combined = new JsonArray();
                var childKeys = libraries.Select(n => LibraryKey((string?)n?["name"] ?? "")).ToHashSet(StringComparer.Ordinal);
                if (parent["libraries"] is JsonArray parents)
                    foreach (var lib in parents.Where(n => !childKeys.Contains(LibraryKey((string?)n?["name"] ?? "")))) combined.Add(lib?.DeepClone());
                foreach (var lib in libraries) combined.Add(lib?.DeepClone());
                result[pair.Key] = combined;
            }
            else if (pair.Key == "arguments" && pair.Value is JsonObject arguments)
            {
                var combined = parent["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var argument in arguments)
                {
                    var values = combined[argument.Key] as JsonArray ?? new JsonArray();
                    if (argument.Value is JsonArray additions) foreach (var value in additions) values.Add(value?.DeepClone());
                    combined[argument.Key] = values;
                }
                result[pair.Key] = combined;
            }
            else if (pair.Value is JsonObject obj && result[pair.Key] is JsonObject old)
            {
                var merged = (JsonObject)old.DeepClone();
                foreach (var value in obj) merged[value.Key] = value.Value?.DeepClone();
                result[pair.Key] = merged;
            }
            else result[pair.Key] = pair.Value?.DeepClone();
        }
        return result;
    }
    private static string LibraryKey(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 3 ? parts[0] + ":" + parts[1] + ":" + string.Join(":", parts.Skip(3)) : name;
    }
}
