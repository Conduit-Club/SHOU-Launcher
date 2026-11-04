using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record InstalledGame(ResolvedVersion Version, LibraryResolution Libraries, AssetIndex Assets,
    string ClientJar, string LibrariesDirectory, string AssetsDirectory, string NativesDirectory,
    IReadOnlyList<string> LegacyExtractions);

public sealed class MinecraftInstaller(HttpClient http, IVerifiedDownloader downloader, string cacheDirectory,
    Func<IReadOnlyList<DownloadSource>> sources)
{
    public async Task<InstalledGame> InstallAsync(ResolvedVersion version, RuleContext context, string gameDirectory,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(gameDirectory);
        var metadata = version.Metadata;
        var client = metadata["downloads"]?["client"] as JsonObject ?? throw new InvalidDataException("Version has no downloadable client.");
        var clientId = (string?)metadata["jar"] ?? version.InheritanceChain.FirstOrDefault() ?? version.Id;
        var clientPath = SafePath.Within(cacheDirectory, $"versions/{SafePath.Identifier(clientId)}/{clientId}.jar");
        await FetchDescriptorAsync(client, clientPath, progress, ct).ConfigureAwait(false);
        var librariesDirectory = Path.Combine(cacheDirectory, "libraries");
        var libraries = LibraryResolver.Resolve(metadata, context);
        await Parallel.ForEachAsync(libraries.Files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) =>
        {
            var path = SafePath.Within(librariesDirectory, file.RelativePath);
            var sha1 = file.Sha1;
            if (string.IsNullOrWhiteSpace(sha1))
            {
                sha1 = await ReadChecksumAsync(file.Url, token).ConfigureAwait(false);
            }
            await downloader.DownloadAsync(new(Map(file.Url), path, sha1, Size: file.Size), progress, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var assetsDirectory = Path.Combine(cacheDirectory, "assets");
        var indexDescriptor = metadata["assetIndex"] as JsonObject ?? throw new InvalidDataException("Version has no asset index descriptor.");
        var indexId = (string?)indexDescriptor["id"] ?? throw new InvalidDataException("Asset index ID missing.");
        var indexPath = SafePath.Within(assetsDirectory, $"indexes/{SafePath.Identifier(indexId)}.json");
        await FetchDescriptorAsync(indexDescriptor, indexPath, progress, ct).ConfigureAwait(false);
        var assets = AssetResolver.Parse(indexId, await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));
        await Parallel.ForEachAsync(assets.Objects.DistinctBy(a => a.Hash), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (asset, token) =>
        {
            await downloader.DownloadAsync(new(Map(asset.OfficialUri), SafePath.Within(assetsDirectory, asset.RelativePath), asset.Hash, Size: asset.Size), progress, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        AssetResolver.MaterializeLegacyLayout(assets, assetsDirectory, gameDirectory);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata.ToJsonString())))[..16].ToLowerInvariant();
        var nativesDirectory = SafePath.Within(gameDirectory, $".shou/natives/{SafePath.Identifier(version.Id)}/{context.OSName}/{context.JavaArchitecture}/{fingerprint}");
        Directory.CreateDirectory(nativesDirectory);
        var extracted = new List<string>();
        foreach (var native in libraries.Files.Where(f => f.LegacyNative))
            extracted.AddRange(await LibraryResolver.ExtractLegacyNativesAsync(SafePath.Within(librariesDirectory, native.RelativePath), nativesDirectory, native.Excludes, ct).ConfigureAwait(false));
        // A completion marker is written only after every verified download and native extraction succeeds.
        var marker = SafePath.Within(gameDirectory, ".shou/install.json");
        await File.WriteAllTextAsync(marker + ".tmp", JsonSerializer.Serialize(new { version = version.Id, javaMajor = version.RequiredJavaMajor,
            os = context.OSName, javaArchitecture = context.JavaArchitecture.ToString(), metadataFingerprint = fingerprint,
            libraries = libraries.Files.Count, assets = assets.Objects.Count, completedAt = DateTimeOffset.UtcNow }), ct).ConfigureAwait(false);
        File.Move(marker + ".tmp", marker, true);
        return new(version, libraries, assets, clientPath, librariesDirectory, assetsDirectory, nativesDirectory, extracted);
    }
    private IReadOnlyList<Uri> Map(Uri uri) => DownloadSources.Resolve([uri.AbsoluteUri], sources()).Select(s => new Uri(s.Url)).ToArray();
    private async Task<string> ReadChecksumAsync(Uri artifact, CancellationToken ct)
    {
        var errors = new List<Exception>();
        var checksumUri = new UriBuilder(artifact); checksumUri.Path += ".sha1";
        foreach (var uri in Map(checksumUri.Uri))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var text = (await http.GetStringAsync(uri, timeout.Token).ConfigureAwait(false)).Trim();
                var checksum = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (checksum?.Length != 40 || checksum.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Library checksum sidecar is invalid.");
                return checksum.ToLowerInvariant();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or IOException or OperationCanceledException)
            { errors.Add(new IOException("Library checksum source failed: " + error.GetType().Name)); }
        }
        throw new AggregateException("No configured source supplied a usable library checksum.", errors);
    }
    private Task FetchDescriptorAsync(JsonObject file, string destination, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var url = (string?)file["url"] ?? throw new InvalidDataException("Download descriptor URL missing.");
        var sha1 = (string?)file["sha1"] ?? throw new InvalidDataException("Download descriptor checksum missing.");
        return downloader.DownloadAsync(new(Map(new Uri(url)), destination, sha1, Size: (long?)file["size"]), progress, ct);
    }
}
