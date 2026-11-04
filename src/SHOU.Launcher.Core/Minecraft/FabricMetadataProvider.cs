using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public sealed class FabricMetadataProvider(HttpClient http, string cacheDirectory, Func<IReadOnlyList<DownloadSource>> sources)
{
    public const int MaximumProfileBytes = 2 * 1024 * 1024;
    public static string ProfileId(string gameVersion, string loaderVersion) =>
        "fabric-loader-" + SafePath.Identifier(loaderVersion) + "-" + SafePath.Identifier(gameVersion);
    public static JsonObject Parse(string text, string gameVersion, string loaderVersion)
    {
        var expected = ProfileId(gameVersion, loaderVersion);
        var profile = VersionMetadataResolver.ParseVersion(text, expected);
        if ((string?)profile["inheritsFrom"] != gameVersion || (string?)profile["mainClass"] != "net.fabricmc.loader.impl.launch.knot.KnotClient")
            throw new InvalidDataException("Fabric profile has an unexpected parent or modern client entry point.");
        if (profile["libraries"] is not JsonArray { Count: > 0 } libraries || libraries.Any(n => n is not JsonObject || string.IsNullOrWhiteSpace((string?)n["name"])))
            throw new InvalidDataException("Fabric profile has no usable library list.");
        return profile;
    }
    public async Task<string> CacheProfileAsync(string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        var id = ProfileId(gameVersion, loaderVersion);
        var path = SafePath.Within(cacheDirectory, $"versions/{id}/{id}.json");
        var digestPath = SafePath.Within(cacheDirectory, $"versions/{id}/{id}.fabric.sha256");
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (!File.Exists(digestPath) || !(await File.ReadAllTextAsync(digestPath, ct).ConfigureAwait(false)).Trim().Equals(Convert.ToHexStringLower(SHA256.HashData(existing)), StringComparison.Ordinal))
                throw new InvalidDataException("Cached Fabric profile checksum is missing or changed; reinstall explicitly.");
            _ = Parse(Encoding.UTF8.GetString(existing), gameVersion, loaderVersion);
            return id;
        }
        var uri = "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(gameVersion) + "/" + Uri.EscapeDataString(loaderVersion) + "/profile/json";
        var errors = new List<Exception>();
        foreach (var source in DownloadSources.Resolve([uri], sources()))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using var response = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaximumProfileBytes) throw new InvalidDataException("Fabric profile exceeds the metadata budget.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[16384];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (count == 0) break;
                    if (output.Length + count > MaximumProfileBytes) throw new InvalidDataException("Fabric profile exceeds the metadata budget.");
                    output.Write(buffer, 0, count);
                }
                var bytes = output.ToArray(); _ = Parse(new UTF8Encoding(false, true).GetString(bytes), gameVersion, loaderVersion);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = SafePath.Within(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(digestPath, Convert.ToHexStringLower(SHA256.HashData(bytes)), ct).ConfigureAwait(false);
                    File.Move(temporary, path, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return id;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or DecoderFallbackException)
            { errors.Add(new IOException("Fabric source failed: " + error.GetType().Name)); }
        }
        throw new AggregateException("No configured Fabric metadata source succeeded.", errors);
    }
}
