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
                            using (OpenPart(destination + ".part", false)) { }
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
    internal static void RejectLink(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!IO.SafePath.Canonical(full).Equals(full, comparison))
            throw new IOException("Download output must not traverse symbolic links/reparse points.");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Download destination or staging sidecar must not be a symbolic link/reparse point.");
    }
    private sealed record ResumeIdentity(string Url, string? ETag);
    private static FileStream OpenPart(string path, bool append)
    {
        RejectLink(path);
        FileStream stream;
        try { stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true); }
        catch (IOException) when (File.Exists(path))
        {
            RejectLink(path);
            // Open does not truncate an existing target. Validate again before any mutation.
            stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None, 131072, true);
        }
        try
        {
            RejectLink(path);
            if (append) stream.Position = stream.Length;
            else stream.SetLength(0);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private static async Task WriteResumeAsync(string path, ResumeIdentity identity, CancellationToken ct)
    {
        RejectLink(path);
        var temporary = path + ".write-" + Guid.NewGuid().ToString("N");
        var owned = false;
        try
        {
            RejectLink(temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                owned = true;
                await JsonSerializer.SerializeAsync(stream, identity, cancellationToken: ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            RejectLink(path); RejectLink(temporary);
            File.Move(temporary, path, true);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
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
        var sourceIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        // Accept an existing pre-migration identity, then rewrite it without signed/query URL text.
        var sameSource = identity?.Url == sourceIdentity || identity?.Url == uri.AbsoluteUri;
        var offset = request.AllowResume && hasHash && sameSource && File.Exists(part) ? new FileInfo(part).Length : 0;
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
        // A network await is a real opportunity for another writer to replace staging paths.
        // Check again before range resets, sidecar writes or opening a writable stream.
        RejectLink(part); RejectLink(resumePath);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            using (OpenPart(part, false)) { }
            throw new IOException("Range is no longer valid; staging reset for next attempt.");
        }
        response.EnsureSuccessStatusCode();
        if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset || (request.Size is { } expected && response.Content.Headers.ContentRange?.Length is { } total && expected != total))
            {
                using (OpenPart(part, false)) { }
                throw new IOException("Invalid resume range.");
            }
        }
        else offset = 0; // A server ignoring Range returned a whole file; truncate first.
        await WriteResumeAsync(resumePath, new(sourceIdentity, response.Headers.ETag?.ToString()), timeout.Token).ConfigureAwait(false);
        await using var output = OpenPart(part, offset > 0);
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
        RejectLink(path);
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
