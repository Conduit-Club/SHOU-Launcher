using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Tests.Java;

// Test transport only: this is not the application's VerifiedDownloader or proxy orchestration.
// Unhashed catalogs are always fresh; immutable files are reused only after hash/size verification.
internal sealed class OnlineRuntimeHttpDownloader(string root) : IVerifiedDownloader, IDisposable
{
    private const long MaximumNetworkBytes = 768L * 1024 * 1024;
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 4
    }) { Timeout = TimeSpan.FromSeconds(90) };
    private readonly ConcurrentQueue<RuntimeHttpReceipt> receipts = new();
    private readonly ConcurrentQueue<RuntimeHttpFailure> failures = new();
    private long networkBytes;
    public bool ForbidRuntimePayloadDownloads { get; set; }
    public IReadOnlyList<RuntimeHttpReceipt> Receipts => receipts.ToArray();
    public IReadOnlyList<RuntimeHttpFailure> Failures => failures.ToArray();
    public long TotalNetworkBytes => Interlocked.Read(ref networkBytes);

    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try { await DownloadCoreAsync(request, progress, cancellationToken); }
        catch (Exception error)
        {
            failures.Enqueue(new(request.Sources.FirstOrDefault()?.AbsoluteUri,
                Path.GetRelativePath(root, request.DestinationPath), error.GetType().FullName));
            throw;
        }
    }

    private async Task DownloadCoreAsync(DownloadRequest request, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Sources.Count != 1) throw new InvalidDataException("Online runtime test expects one official URL per request.");
        var uri = request.Sources[0];
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.Host is not
                ("piston-meta.mojang.com" or "piston-data.mojang.com" or "launcher.mojang.com" or "launchermeta.mojang.com"))
            throw new InvalidDataException("Online runtime test permits only credential-free official Mojang HTTPS URLs.");
        var path = Contained(request.DestinationPath);
        var metadata = Path.GetRelativePath(root, path).StartsWith("metadata" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (File.Exists(path) && (request.Sha1 is not null || request.Sha256 is not null))
        {
            var cached = await HashFileAsync(path, cancellationToken);
            if (Matches(request, cached))
            {
                receipts.Enqueue(Receipt(uri, path, request, cached, cacheHit: true, metadata));
                return;
            }
            throw new InvalidDataException("Existing online-test cache failed verification; preserve it for diagnosis.");
        }
        if (ForbidRuntimePayloadDownloads && !metadata)
            throw new InvalidDataException("The second installation attempted to download a runtime payload again.");
        var limit = metadata ? 32L * 1024 * 1024 : 256L * 1024 * 1024;
        if (request.Size is < 0 || request.Size > limit) throw new InvalidDataException("Online runtime file exceeds the test budget.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".online-part-" + Guid.NewGuid().ToString("N");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode(); // Redirects are disabled; TLS certificate validation stays at platform defaults.
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Online runtime test requires HTTP 200.");
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Online runtime response exceeds the test budget.");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            var buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                bytes = checked(bytes + count);
                var aggregateBytes = Interlocked.Add(ref networkBytes, count);
                if (bytes > limit || aggregateBytes > MaximumNetworkBytes)
                    throw new InvalidDataException("Online runtime download exceeded its bounded test budget.");
                sha1.AppendData(buffer, 0, count); sha256.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                progress?.Report(new TransferProgress(path, "Downloading", bytes, request.Size, uri.Host));
            }
        }
        var actual = new RuntimeFileHashes(bytes, Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
        if (!Matches(request, actual)) throw new InvalidDataException("Official runtime response hash/size does not match the descriptor; partial bytes were preserved.");
        File.Move(temporary, path, true);
        receipts.Enqueue(Receipt(uri, path, request, actual, cacheHit: false, metadata));
        progress?.Report(new TransferProgress(path, "Completed", bytes, request.Size, uri.Host));
    }

    public static async Task<RuntimeFileHashes> HashFileAsync(string path, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var buffer = new byte[65536]; long bytes = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            bytes = checked(bytes + count); sha1.AppendData(buffer, 0, count); sha256.AppendData(buffer, 0, count);
        }
        return new(bytes, Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private RuntimeHttpReceipt Receipt(Uri uri, string path, DownloadRequest request, RuntimeFileHashes actual,
        bool cacheHit, bool metadata) => new(uri.AbsoluteUri, Path.GetRelativePath(root, path), request.Sha1,
            request.Sha256, request.Size, actual.Size, actual.Sha1, actual.Sha256, cacheHit, metadata,
            cacheHit ? 0 : actual.Size);
    private static bool Matches(DownloadRequest request, RuntimeFileHashes actual) =>
        (!request.Size.HasValue || request.Size == actual.Size)
        && (request.Sha1 is null || request.Sha1.Equals(actual.Sha1, StringComparison.OrdinalIgnoreCase))
        && (request.Sha256 is null || request.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase));
    private string Contained(string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Online runtime destination escapes the independent test root.");
        var current = Path.GetFullPath(root);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw new IOException("Online runtime download destination cannot traverse a symbolic link.");
        }
        return current;
    }
    public void Dispose() => http.Dispose();
}

internal sealed record RuntimeFileHashes(long Size, string Sha1, string Sha256);
internal sealed record RuntimeHttpReceipt(string Url, string DestinationRelativePath, string? ExpectedSha1,
    string? ExpectedSha256, long? ExpectedSize, long Size, string Sha1, string Sha256, bool CacheHit,
    bool Metadata, long NetworkBytes);
internal sealed record RuntimeHttpFailure(string? Url, string DestinationRelativePath, string? ErrorType);
