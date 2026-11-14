using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadTests
{
    [TestMethod] public async Task RealHttpFallbackAndHashVerification()
    {
        var bytes = Encoding.UTF8.GetBytes("中文 library bytes (fixture)");
        using var server = new LocalHttpFixture(context =>
        {
            if (context.Request.Url!.AbsolutePath == "/missing") { context.Response.StatusCode = 404; context.Response.Close(); return; }
            context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes); context.Response.Close();
        });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var path = Path.Combine(TestFiles.Root(), "中文 空格", "client.jar");
        await new VerifiedDownloader(http).DownloadAsync(new([server.Uri("missing"), server.Uri("ok")], path,
            Sha1: Convert.ToHexString(SHA1.HashData(bytes)), Size: bytes.Length));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }
    [TestMethod] public async Task IgnoredRangeTruncatesStagingAndHashFailureNeverReplacesInstalledFile()
    {
        var bytes = Encoding.UTF8.GetBytes("complete fixture");
        using var server = new LocalHttpFixture(context => { context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes); context.Response.Close(); });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var path = Path.Combine(TestFiles.Root(), "client.jar");
        await File.WriteAllBytesAsync(path + ".part", bytes[..4]);
        await File.WriteAllTextAsync(path + ".part.resume.json", System.Text.Json.JsonSerializer.Serialize(new { Url = server.Uri("file").AbsoluteUri, ETag = (string?)null }));
        var downloader = new VerifiedDownloader(http);
        await downloader.DownloadAsync(new([server.Uri("file")], path, Sha1: Convert.ToHexString(SHA1.HashData(bytes)), Size: bytes.Length));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsExactlyAsync<AggregateException>(() => downloader.DownloadAsync(new([server.Uri("file")], path, Sha1: new string('0', 40), Size: bytes.Length)));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }
    [TestMethod] public void ArchiveAndSymbolicLinkEscapesAreRejected()
    {
        var root = TestFiles.Root();
        Assert.ThrowsExactly<IOException>(() => SafePath.Within(root, "../escape"));
        Assert.ThrowsExactly<IOException>(() => SafePath.Within(root, "C:\\escape"));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.GetTempPath());
            Assert.ThrowsExactly<IOException>(() => SafePath.Within(root, "link/escape"));
        }
    }
}

internal sealed class LocalHttpFixture : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly int port;
    public LocalHttpFixture(Action<HttpListenerContext> respond)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { respond(await listener.GetContextAsync().WaitAsync(stop.Token)); }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) when (stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
            }
        });
    }
    public Uri Uri(string relative) => new($"http://127.0.0.1:{port}/" + relative);
    public void Dispose() { stop.Cancel(); listener.Close(); loop.GetAwaiter().GetResult(); stop.Dispose(); }
}
