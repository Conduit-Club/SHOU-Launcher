using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class Aria2ConnectionTests
{
    [TestMethod]
    public async Task FailedProbeIsSharedAndExplicitTestReconnectsWithoutLaunchingAnything()
    {
        var calls = 0; var available = false;
        using var server = new LocalHttpFixture(context =>
        {
            Interlocked.Increment(ref calls);
            using var body = JsonDocument.Parse(context.Request.InputStream);
            var id = body.RootElement.GetProperty("id").GetString();
            Assert.AreEqual("aria2.getVersion", body.RootElement.GetProperty("method").GetString());
            Respond(context, available ? new { jsonrpc = "2.0", id, result = (object)new { version = "1.37.0" } }
                : new { jsonrpc = "2.0", id, result = (object)new { unexpected = true } });
        });
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, server.Uri("jsonrpc").AbsoluteUri)));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => options.Aria2Connection.ConnectAsync(options, options.Capture())));
        Assert.IsTrue(results.All(r => r is null)); Assert.AreEqual(1, calls);
        available = true;
        var status = await options.Aria2Connection.TestAsync(options);
        Assert.IsTrue(status.Connected); Assert.AreEqual("1.37.0", status.Version); Assert.AreEqual(2, calls);
        Assert.IsNotNull(await options.Aria2Connection.ConnectAsync(options, options.Capture())); Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task UnavailableOrWrongRpcUsesBuiltinWithoutAddingJobsOrExposingResponse()
    {
        using var server = new LocalHttpFixture(context =>
        {
            using var body = JsonDocument.Parse(context.Request.InputStream);
            Respond(context, new { jsonrpc = "2.0", id = body.RootElement.GetProperty("id").GetString(), error = new { code = 1, message = "synthetic-secret-must-not-be-displayed" } });
        });
        foreach (var endpoint in new[] { server.Uri("jsonrpc").AbsoluteUri, "http://127.0.0.1:1/jsonrpc", "invalid", "http://remote.invalid/jsonrpc" })
        {
            var options = new NetworkOptions(); options.Update(new(Aria2: new(true, endpoint)));
            var status = await options.Aria2Connection.TestAsync(options);
            Assert.IsFalse(status.Connected); Assert.IsFalse(status.Message.Contains("synthetic-secret"));
            var fallback = new Fallback(); using var http = new HttpClient();
            await new DownloadCoordinator(http, fallback, options).DownloadAsync(new([new Uri("https://fixture.invalid/file")], Path.Combine(TestFiles.Root(), "file"), Sha256: new string('a', 64)));
            Assert.AreEqual(1, fallback.Calls);
        }
    }

    [TestMethod]
    public async Task RpcRedirectIsNotFollowedAndCallerCancellationIsNotCached()
    {
        var forwarded = 0;
        using var other = new LocalHttpFixture(context => { Interlocked.Increment(ref forwarded); context.Response.Close(); });
        using var server = new LocalHttpFixture(context => { context.Response.StatusCode = 307; context.Response.RedirectLocation = other.Uri("jsonrpc").AbsoluteUri; context.Response.Close(); });
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, server.Uri("jsonrpc").AbsoluteUri)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => options.Aria2Connection.ConnectAsync(options, options.Capture(), cancelled.Token));
        Assert.IsFalse((await options.Aria2Connection.TestAsync(options)).Connected); Assert.AreEqual(0, forwarded);
    }

    private static void Respond(HttpListenerContext context, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes); context.Response.Close();
    }
    private sealed class Fallback : IVerifiedDownloader
    {
        public int Calls;
        public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }
    }
}
