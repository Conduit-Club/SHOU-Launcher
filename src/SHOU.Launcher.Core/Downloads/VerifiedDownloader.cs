using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

public sealed class VerifiedDownloader(HttpClient http) : IVerifiedDownloader
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (request.Sources.Count == 0) throw new ArgumentException("Download has no sources.");
        var destination = Path.GetFullPath(request.DestinationPath);
        foreach (var candidate in new[] { destination, destination + ".download.lock", destination + ".part", destination + ".part.resume.json" })
            RejectLink(candidate);
        var gate = Locks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            foreach (var candidate in new[] { destination, destination + ".download.lock", destination + ".part", destination + ".part.resume.json" })
                RejectLink(candidate);
            await using var fileLease = new FileStream(destination + ".download.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await IsValidAsync(destination, request, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new(destination, "Completed", new FileInfo(destination).Length, request.Size, "cache"));
                return;
            }
            var failures = new List<Exception>();
            foreach (var uri in request.Sources)
            {
                if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Only HTTP(S) downloads without URL credentials are supported.");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await DownloadOneAsync(uri, destination, request, progress, cancellationToken).ConfigureAwait(false);
                        var checksummed = request.Sha1 is not null || request.Sha256 is not null;
                        var valid = checksummed ? await IsValidAsync(destination + ".part", request, cancellationToken).ConfigureAwait(false)
                            : request.Size is not { } declaredSize || new FileInfo(destination + ".part").Length == declaredSize;
                        if (!valid)
                        {
                            // This is owned, reproducible staging, never an installed file.
                            using (File.Create(destination + ".part")) { }
                            throw new InvalidDataException("Downloaded file failed size/hash verification.");
                        }
                        File.Move(destination + ".part", destination, true);
                        progress?.Report(new(destination, "Completed", new FileInfo(destination).Length, request.Size, uri.Host));
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                    {
                        // Do not expose signed URLs, headers or response bodies in errors.
                        failures.Add(new IOException($"Download source {uri.Host} attempt {attempt + 1}: {ex.GetType().Name}"));
                    }
                }
            }
            throw new AggregateException("All download sources failed; installed file was not replaced.", failures);
        }
        finally { gate.Release(); }
    }
    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Download destination or staging sidecar must not be a symbolic link/reparse point.");
    }
    private sealed record ResumeIdentity(string Url, string? ETag);
    private async Task DownloadOneAsync(Uri uri, string destination, DownloadRequest request, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var part = destination + ".part";
        var resumePath = part + ".resume.json";
        RejectLink(part); RejectLink(resumePath);
        ResumeIdentity? identity = null;
        if (File.Exists(resumePath))
        {
            try { identity = JsonSerializer.Deserialize<ResumeIdentity>(await File.ReadAllTextAsync(resumePath, ct).ConfigureAwait(false)); }
            catch (JsonException) { /* Invalid staging identity starts a new transfer. */ }
        }
        var hasHash = !string.IsNullOrEmpty(request.Sha1) || !string.IsNullOrEmpty(request.Sha256);
        var offset = request.AllowResume && hasHash && identity?.Url == uri.AbsoluteUri && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (request.Size is { } size && offset > size) offset = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
            if (identity?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var parsed)) message.Headers.IfRange = new RangeConditionHeaderValue(parsed);
        }
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            using (File.Create(part)) { }
            throw new IOException("Range is no longer valid; staging reset for next attempt.");
        }
        response.EnsureSuccessStatusCode();
        if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset || (request.Size is { } expected && response.Content.Headers.ContentRange?.Length is { } total && expected != total))
            {
                using (File.Create(part)) { }
                throw new IOException("Invalid resume range.");
            }
        }
        else offset = 0; // A server ignoring Range returned a whole file; truncate first.
        await File.WriteAllTextAsync(resumePath, JsonSerializer.Serialize(new ResumeIdentity(uri.AbsoluteUri, response.Headers.ETag?.ToString())), ct).ConfigureAwait(false);
        await using var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var bytes = offset;
        var totalBytes = request.Size ?? (response.Content.Headers.ContentLength is { } len ? len + offset : null);
        var buffer = new byte[131072];
        var lastProgress = DateTimeOffset.MinValue;
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            bytes += read;
            if (request.Size is { } limit && bytes > limit) throw new InvalidDataException("Download exceeded declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow - lastProgress > TimeSpan.FromMilliseconds(100))
            { progress?.Report(new(destination, "Downloading", bytes, totalBytes, uri.Host)); lastProgress = DateTimeOffset.UtcNow; }
        }
        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
    }
    public static async Task<bool> IsValidAsync(string path, DownloadRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return false;
        if (request.Size is { } length && new FileInfo(path).Length != length) return false;
        if (request.Sha1 is null && request.Sha256 is null) return false; // Unhashed catalogs are fetched fresh.
        await using var input = File.OpenRead(path);
        if (request.Sha1 is { } sha1 && !Convert.ToHexString(await SHA1.HashDataAsync(input, ct).ConfigureAwait(false)).Equals(sha1, StringComparison.OrdinalIgnoreCase)) return false;
        input.Position = 0;
        if (request.Sha256 is { } sha256 && !Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
