using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

public sealed partial class VerifiedDownloader
{
    private const int RangeChunkBytes = 1024 * 1024;
    private const long RangeMinimumBytes = 8L * 1024 * 1024;

    // Keep only one window of chunks in memory and consume it in file order. This lets
    // hashes run alongside writes, and a cancelled transfer leaves a resumable prefix.
    private async Task<bool> TryDownloadRangesAsync(Uri uri, string destination, DownloadRequest request, DownloadBehavior policy,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (request.Size is not { } size || size < RangeMinimumBytes || policy.MaximumConnections < 2
            || request.Sha1 is null && request.Sha256 is null) return false;
        var part = destination + ".part"; var resumePath = part + ".resume.json";
        RejectLink(part); RejectLink(resumePath);
        ResumeIdentity? identity = null;
        if (File.Exists(resumePath))
        {
            try { identity = JsonSerializer.Deserialize<ResumeIdentity>(await File.ReadAllTextAsync(resumePath, ct).ConfigureAwait(false)); }
            catch (JsonException) { }
        }
        var sourceIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var sameSource = identity?.Url == sourceIdentity || identity?.Url == uri.AbsoluteUri;
        var offset = request.AllowResume && sameSource && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > size) offset = 0;
        if (offset == size)
        {
            if (await IsValidAsync(part, request, ct).ConfigureAwait(false)) return true;
            offset = 0;
        }
        var maximum = Math.Clamp(policy.MaximumConnections, 1, DownloadDefaults.MaximumConnections);
        var connections = (int)Math.Min(Math.Min(DownloadDefaults.ConnectionsPerFile, maximum), (size - offset + RangeChunkBytes - 1) / RangeChunkBytes);
        if (connections < 2) return false;
        // Reserve the whole window at once: workers never hold one slot while waiting
        // for the rest, which would deadlock when several large files start together.
        using var lease = await Budget.EnterAsync(maximum, connections, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rate = policy.MaximumKiBPerSecond;
        var idleTimeout = TimeSpan.FromMinutes(rate > 0 ? 30 : 3);
        timeout.CancelAfter(idleTimeout);
        using var probe = new HttpRequestMessage(HttpMethod.Get, uri);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        probe.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        // A rejected probe does not tell us whether a regular GET can succeed.
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != 0 || range.To != 0 || range.Length != size
            || response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) return false;
        await using (var probeStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await probeStream.ReadExactlyAsync(new byte[1], timeout.Token).ConfigureAwait(false);
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag : null;
        if (offset > 0 && identity?.ETag is { } previous && etag is not null && previous != etag.ToString()) offset = 0;
        RejectLink(part); RejectLink(resumePath);
        await WriteResumeAsync(resumePath, new(sourceIdentity, etag?.ToString()), timeout.Token).ConfigureAwait(false);
        await using var output = OpenPart(part, offset > 0);
        using var hash = new TransferHash(request);
        await hash.ReadPrefixAsync(output, offset, timeout.Token).ConfigureAwait(false);
        var queued = new Queue<Task<byte[]>>();
        var all = new List<Task<byte[]>>(connections);
        long next = offset, written = offset, received = offset;
        var active = 0; var progressSync = new object(); var lastProgress = DateTimeOffset.MinValue;
        void Report(int count, bool force = false)
        {
            lock (progressSync)
            {
                received += count;
                if (force || DateTimeOffset.UtcNow - lastProgress >= TimeSpan.FromMilliseconds(100))
                {
                    progress?.Report(new(destination, "Downloading", received, size, uri.Host, "内置",
                        $"分段下载 · {connections} 路", Math.Max(1, Volatile.Read(ref active))));
                    lastProgress = DateTimeOffset.UtcNow;
                }
            }
        }
        async Task<byte[]> ReadChunkAsync(long start, int length)
        {
            Interlocked.Increment(ref active);
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                readTimeout.CancelAfter(idleTimeout);
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                message.Headers.Range = new RangeHeaderValue(start, start + length - 1);
                message.Headers.AcceptEncoding.ParseAdd("identity");
                if (etag is not null) message.Headers.IfRange = new RangeConditionHeaderValue(etag);
                using var chunk = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token).ConfigureAwait(false);
                var contentRange = chunk.Content.Headers.ContentRange;
                if (chunk.StatusCode != HttpStatusCode.PartialContent || contentRange?.From != start || contentRange.To != start + length - 1 || contentRange.Length != size
                    || chunk.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new RangeUnavailableException();
                var buffer = new byte[length]; var count = 0;
                await using var input = await chunk.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
                while (count < length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(count, Math.Min(131072, length - count)), readTimeout.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Download range ended early.");
                    readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await Budget.ThrottleAsync(read, rate, timeout.Token).ConfigureAwait(false);
                    count += read; Report(read); readTimeout.CancelAfter(idleTimeout);
                }
                if (await input.ReadAsync(new byte[1], readTimeout.Token).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Download range exceeded its declared length.");
                return buffer;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        void Enqueue()
        {
            if (next >= size) return;
            var length = (int)Math.Min(RangeChunkBytes, size - next);
            var task = ReadChunkAsync(next, length); next += length;
            queued.Enqueue(task); all.Add(task);
        }
        try
        {
            for (var i = 0; i < connections; i++) Enqueue();
            while (queued.TryDequeue(out var task))
            {
                var bytes = await task.ConfigureAwait(false);
                all.Remove(task);
                await output.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                hash.Append(bytes); written += bytes.Length;
                Enqueue();
            }
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            if (!hash.Matches(written))
            {
                output.SetLength(0);
                throw new InvalidDataException("Downloaded file failed size/hash verification.");
            }
            Report(0, true);
            return true;
        }
        catch (RangeUnavailableException) { return false; } // Some CDNs ignore range requests after the probe.
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(all).ConfigureAwait(false); } catch { /* Preserve the original failure/cancellation. */ }
        }
    }
    private sealed class RangeUnavailableException : IOException { }
}
