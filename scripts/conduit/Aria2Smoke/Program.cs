using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PCL.Core.Conduit;

if (args.Length != 2) throw new ArgumentException("Usage: Aria2Smoke <aria2c.exe> <new-output-directory>");
var directory = Path.GetFullPath(args[1]);
if (Directory.Exists(directory)) throw new IOException("Use a new output directory for each smoke run.");
Directory.CreateDirectory(directory);
int Port()
{
    var socket = new TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
    socket.Stop();
    return port;
}
var serverPort = Port();
var rpcPort = Port();
var bytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
var requestedRanges = new List<long>();
var missingRequests = 0;
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{serverPort}/");
listener.Start();
async Task Serve()
{
    while (!lifetime.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        _ = Task.Run(async () =>
        {
            try
            {
                if (context.Request.Url!.AbsolutePath == "/missing")
                {
                    Interlocked.Increment(ref missingRequests);
                    context.Response.StatusCode = 404;
                    return;
                }
                var start = 0L;
                var range = context.Request.Headers["Range"];
                if (range != null)
                {
                    start = long.Parse(range.Split('=')[1].Split('-')[0]);
                    context.Response.StatusCode = 206;
                    context.Response.Headers["Content-Range"] = $"bytes {start}-{bytes.Length - 1}/{bytes.Length}";
                }
                lock (requestedRanges) requestedRanges.Add(start);
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.ContentLength64 = bytes.Length - start;
                if (context.Request.HttpMethod == "HEAD") return;
                for (var offset = (int)start; offset < bytes.Length; offset += 32768)
                {
                    await context.Response.OutputStream.WriteAsync(bytes.AsMemory(offset, Math.Min(32768, bytes.Length - offset)), lifetime.Token);
                    await Task.Delay(10, lifetime.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException) { }
            finally { context.Response.Close(); }
        });
    }
}
var server = Serve();
var secret = Guid.NewGuid().ToString("N");
var startInfo = new ProcessStartInfo(Path.GetFullPath(args[0])) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false };
foreach (var option in new[] { "--enable-rpc=true", "--rpc-listen-all=false", $"--rpc-listen-port={rpcPort}", $"--rpc-secret={secret}", "--console-log-level=error", "--summary-interval=0" }) startInfo.ArgumentList.Add(option);
using var aria = Process.Start(startInfo) ?? throw new IOException("aria2 failed to start.");
try
{
    using var http = new HttpClient();
    var rpc = new Aria2RpcClient(http, new Uri($"http://127.0.0.1:{rpcPort}/jsonrpc"), secret);
    for (var attempt = 0; ; attempt++)
        try { await rpc.GetVersionAsync(lifetime.Token); break; }
        catch (HttpRequestException) when (attempt < 20) { await Task.Delay(200, lifetime.Token); }
    var path = Path.Combine(directory, "resume.aria2.part");
    string[] urls = [$"http://127.0.0.1:{serverPort}/missing", $"http://127.0.0.1:{serverPort}/file"];
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    try
    {
        await rpc.DownloadAsync(urls, path, "SHOU-Aria2-Smoke", progress => { if (progress.Completed > 512 * 1024) cancel.Cancel(); }, cancel.Token);
        throw new Exception("Download unexpectedly finished before cancellation.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    if (!File.Exists(path + ".aria2")) throw new Exception("aria2 did not retain its resume control file.");
    await rpc.DownloadAsync(urls, path, "SHOU-Aria2-Smoke", null, lifetime.Token);
    var downloaded = await File.ReadAllBytesAsync(path, lifetime.Token);
    if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(downloaded))) throw new Exception("SHA-256 mismatch.");
    if (missingRequests == 0 || !requestedRanges.Any(n => n > 0)) throw new Exception("404 fallback or Range resume was not exercised.");
    await rpc.ForgetCompletedAsync(path, lifetime.Token);
    Console.WriteLine("PASS: real aria2 RPC; 404 fallback, cancellation/pause, reattach, HTTP Range resume, SHA-256 match.");
}
finally
{
    if (!aria.HasExited) { aria.Kill(); await aria.WaitForExitAsync(); }
    lifetime.Cancel();
    listener.Stop();
    try { await server; } catch (HttpListenerException) { }
}
