using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SHOU.Launcher.Core.Downloads;

/// <summary>A single fixed resource through its existing allowlisted transport. No arbitrary URL proxy or credentials.</summary>
internal sealed class RestrictedDownloadRelay : IAsyncDisposable
{
    private readonly HttpClient transport;
    private readonly Uri source;
    private readonly long size;
    private readonly NetworkOptions network;
    private readonly SemaphoreSlim slots = new(8, 8);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, Task> requests = new();
    private readonly Task accepting;
    private int next;
    private readonly string route = "/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "/file";
    public Uri Uri { get; }
    public RestrictedDownloadRelay(HttpClient transport, Uri source, long size, NetworkOptions network)
    {
        this.transport = transport; this.source = source; this.size = size; this.network = network;
        listener.Start(); Uri = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{route}");
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await slots.WaitAsync(lifetime.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false); }
                catch { slots.Release(); throw; }
                var id = Interlocked.Increment(ref next); var task = ServeLimitedAsync(client);
                requests[id] = task;
                _ = task.ContinueWith(_ => requests.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task ServeLimitedAsync(TcpClient client)
    { try { await ServeAsync(client).ConfigureAwait(false); } finally { slots.Release(); } }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var data = new byte[16384]; var count = 0;
                // Read only through the header boundary; never accept a request body or a persistent pipeline.
                while (count < data.Length)
                {
                    var read = await stream.ReadAsync(data.AsMemory(count, 1), headerTimeout.Token).ConfigureAwait(false);
                    if (read == 0) return; count++;
                    if (count >= 4 && data[count - 4] == 13 && data[count - 3] == 10 && data[count - 2] == 13 && data[count - 1] == 10) break;
                }
                if (count == data.Length) return;
                var lines = Encoding.ASCII.GetString(data, 0, count).Split("\r\n"); var first = lines[0].Split(' ');
                if (first.Length != 3 || first[1] != route || first[0] is not ("GET" or "HEAD") || first[2] != "HTTP/1.1") { await EmptyAsync(stream, 404).ConfigureAwait(false); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
                { var colon = line.IndexOf(':'); if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) { await EmptyAsync(stream, 400).ConfigureAwait(false); return; } }
                if (!headers.TryGetValue("Host", out var host) || host != Uri.Authority || headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out var body) && body != "0")
                { await EmptyAsync(stream, 400).ConfigureAwait(false); return; }
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (headers.TryGetValue("Range", out var range))
                {
                    if (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Ranges.Count != 1 || parsed.Unit != "bytes" || parsed.Ranges.Any(r => r.From >= size || r.To >= size))
                    { await EmptyAsync(stream, 416).ConfigureAwait(false); return; }
                    request.Headers.Range = parsed;
                }
                if (headers.TryGetValue("If-Range", out var validator))
                { if (!RangeConditionHeaderValue.TryParse(validator, out var parsed)) { await EmptyAsync(stream, 400).ConfigureAwait(false); return; } request.Headers.IfRange = parsed; }
                using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) { await EmptyAsync(stream, (int)response.StatusCode).ConfigureAwait(false); return; }
                var contentRange = response.Content.Headers.ContentRange;
                var length = response.Content.Headers.ContentLength ?? (response.StatusCode == HttpStatusCode.OK ? size : contentRange?.To - contentRange?.From + 1);
                if (length is null or <= 0 || length > size || response.StatusCode == HttpStatusCode.PartialContent && (contentRange?.Length != size || contentRange.From is null || contentRange.To is null))
                { await EmptyAsync(stream, 502).ConfigureAwait(false); return; }
                var reply = $"HTTP/1.1 {(int)response.StatusCode} OK\r\nConnection: close\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\n";
                if (contentRange is not null) reply += "Content-Range: " + contentRange + "\r\n";
                if (response.Headers.ETag is { } etag) reply += "ETag: " + etag + "\r\n";
                if (response.Content.Headers.LastModified is { } modified) reply += "Last-Modified: " + modified.ToString("R") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\r\n"), lifetime.Token).ConfigureAwait(false);
                if (first[0] == "HEAD") return;
                await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token).ConfigureAwait(false);
                var buffer = new byte[65536]; var remaining = length.Value;
                while (remaining > 0)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    await network.Budget.ThrottleAsync(read, network.Capture().Downloads?.MaximumKiBPerSecond ?? 0, idle.Token).ConfigureAwait(false);
                    await stream.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false); remaining -= read;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            { /* Closing a failed/short transfer lets aria2 retry; URLs, keys and response bodies never enter logs. */ }
        }
    }
    private static Task EmptyAsync(NetworkStream stream, int status) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Failed\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")).AsTask();
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); await accepting.ConfigureAwait(false);
        await Task.WhenAll(requests.Values).ConfigureAwait(false); lifetime.Dispose();
    }
}
