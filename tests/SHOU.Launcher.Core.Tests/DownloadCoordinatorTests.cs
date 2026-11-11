using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadCoordinatorTests
{
    [TestMethod]
    public async Task LocalRpcCompletesOnceAndConcurrentCallerUsesVerifiedCache()
    {
        var bytes = Encoding.UTF8.GetBytes("RPC verified fixture 中文");
        var destination = Path.Combine(TestFiles.Root(), "rpc-library.jar");
        using var rpc = new RpcFixture(bytes);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, rpc.Endpoint.AbsoluteUri)));
        var fallback = new RecordingDownloader();
        var coordinator = new DownloadCoordinator(http, fallback, options);
        var request = new DownloadRequest([new Uri("https://library.invalid/example.jar")], destination,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length);
        await Task.WhenAll(coordinator.DownloadAsync(request), coordinator.DownloadAsync(request));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(1, rpc.AddCount); Assert.AreEqual(1, rpc.ForgetCount); Assert.AreEqual(0, fallback.Calls);
        Assert.AreEqual(destination + ".aria2-part", rpc.Path);
    }

    [TestMethod]
    public async Task NonDefaultConcurrencyKeepsRpcAndReservesEverySegment()
    {
        var bytes = new byte[16 * 1024 * 1024];
        var destination = Path.Combine(TestFiles.Root(), "segmented.jar");
        using var rpc = new RpcFixture(bytes);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, rpc.Endpoint.AbsoluteUri), Downloads: new(MaximumConnections: 2)));
        var fallback = new RecordingDownloader(); var progress = new List<TransferProgress>();
        await new DownloadCoordinator(http, fallback, options).DownloadAsync(new([new Uri("https://one.invalid/file"), new Uri("https://two.invalid/file")], destination,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length), new Callback(progress.Add));
        Assert.AreEqual("2", rpc.Options!.Value.GetProperty("split").GetString());
        Assert.AreEqual("2", rpc.Options.Value.GetProperty("max-connection-per-server").GetString());
        Assert.AreEqual(2, rpc.SourceCount); Assert.AreEqual(0, fallback.Calls);
        Assert.IsTrue(progress.Any(p => p.Stage == "Verifying" && p.Backend == "aria2"));
        using var allSlots = await options.Budget.EnterAsync(2, 2, CancellationToken.None);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = options.Budget.EnterAsync(2, queuedCancellation.Token);
        Assert.IsFalse(queued.IsCompleted); queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
    }

    [TestMethod]
    public async Task StrictPolicyExplainsItsInternalBackendInsteadOfSilentlyIgnoringAria2()
    {
        foreach (var policy in new[] { new DownloadBehavior(MaximumKiBPerSecond: 100), new DownloadBehavior(HttpsOnly: true) })
        {
            var options = new NetworkOptions(); options.Update(new(Aria2: new(true, "not-used"), Downloads: policy));
            using var http = new HttpClient(); var fallback = new ProgressDownloader(); var reports = new List<TransferProgress>();
            await new DownloadCoordinator(http, fallback, options).DownloadAsync(new([new Uri("https://test.invalid/file")], Path.Combine(TestFiles.Root(), "file"), Sha1: new string('a', 40)), new Callback(reports.Add));
            Assert.AreEqual("内置", reports.Single().Backend); Assert.IsFalse(string.IsNullOrWhiteSpace(reports.Single().Detail));
        }
    }
    private sealed class Callback(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress progress) => report(progress); }
    private sealed class ProgressDownloader : IVerifiedDownloader
    {
        public Task DownloadAsync(DownloadRequest r, IProgress<TransferProgress>? p = null, CancellationToken ct = default)
        { p?.Report(new(r.DestinationPath, "Completed", 1, 1)); return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task RpcHashMismatchPreservesInstalledFileUntilInternalVerifiedFallback()
    {
        var valid = Encoding.UTF8.GetBytes("expected library fixture");
        var invalid = Encoding.UTF8.GetBytes("untrusted rpc output fixture");
        var destination = Path.Combine(TestFiles.Root(), "client.jar");
        await File.WriteAllTextAsync(destination, "previous installed bytes");
        using var rpc = new RpcFixture(invalid);
        using var download = new LocalHttpFixture(context =>
        { context.Response.ContentLength64 = valid.Length; context.Response.OutputStream.Write(valid); context.Response.Close(); });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, rpc.Endpoint.AbsoluteUri)));
        var warnings = new List<string>();
        await new DownloadCoordinator(http, new VerifiedDownloader(http), options, warnings.Add).DownloadAsync(
            new([download.Uri("client")], destination, Sha256: Convert.ToHexString(SHA256.HashData(valid)), Size: valid.Length));
        CollectionAssert.AreEqual(valid, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(1, rpc.AddCount); Assert.AreEqual(0, rpc.ForgetCount);
        Assert.HasCount(1, warnings); StringAssert.Contains(warnings[0], "内部下载器");
    }

    [TestMethod]
    public async Task UnhashedCatalogDoesNotEnterResumableRpc()
    {
        var options = new NetworkOptions(); options.Update(new(Aria2: new(true, "not-an-endpoint")));
        using var http = new HttpClient(); var fallback = new RecordingDownloader();
        await new DownloadCoordinator(http, fallback, options).DownloadAsync(
            new([new Uri("https://catalog.invalid/catalog.json")], Path.Combine(TestFiles.Root(), "catalog.json")));
        Assert.AreEqual(1, fallback.Calls);
    }

    [TestMethod]
    public async Task ResumeReceiptStoresSourceDigestWithoutSignedQueryText()
    {
        var bytes = Encoding.UTF8.GetBytes("signed download fixture");
        using var server = new LocalHttpFixture(context =>
        { context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes); context.Response.Close(); });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var destination = Path.Combine(TestFiles.Root(), "client.jar");
        await new VerifiedDownloader(http).DownloadAsync(new([server.Uri("file?ticket=synthetic-private-query")], destination,
            Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length));
        var receipt = await File.ReadAllTextAsync(destination + ".part.resume.json");
        StringAssert.Contains(receipt, "sha256:");
        Assert.IsFalse(receipt.Contains("synthetic-private-query", StringComparison.Ordinal));
        Assert.IsFalse(receipt.Contains("http://", StringComparison.Ordinal));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
    }

    [TestMethod]
    public async Task DestinationAndRpcSidecarLinksFailBeforeAnyTransport()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Creating native Windows links requires a separately provisioned native fixture.");
        foreach (var suffix in new[] { "", ".aria2-part", ".aria2-part.aria2", ".coordinator.lock" })
        {
            var root = TestFiles.Root(); var destination = Path.Combine(root, "client.jar");
            var outside = Path.Combine(TestFiles.Root(), "protected-test-bytes");
            await File.WriteAllTextAsync(outside, "must remain unchanged");
            File.CreateSymbolicLink(destination + suffix, outside);
            var options = new NetworkOptions(); options.Update(new(Aria2: new(true)));
            using var http = new HttpClient(); var fallback = new RecordingDownloader();
            await Assert.ThrowsExactlyAsync<IOException>(() => new DownloadCoordinator(http, fallback, options).DownloadAsync(
                new([new Uri("https://library.invalid/file")], destination, Sha256: new string('A', 64))));
            Assert.AreEqual(0, fallback.Calls); Assert.AreEqual("must remain unchanged", await File.ReadAllTextAsync(outside));
        }
    }

    [TestMethod]
    public async Task InternalDownloadAndCacheValidationRejectLinkedAncestor()
    {
        var root = TestFiles.Root(); var other = TestFiles.Root();
        var target = Path.Combine(other, "client.jar"); await File.WriteAllTextAsync(target, "untouched");
        if (OperatingSystem.IsWindows()) await NativeWindowsLinks.CreateDirectoryJunctionAsync(Path.Combine(root, "link"), other);
        else Directory.CreateSymbolicLink(Path.Combine(root, "link"), other);
        var request = new DownloadRequest([new Uri("https://library.invalid/file")], Path.Combine(root, "link/client.jar"),
            Sha256: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("untouched"))));
        using var http = new HttpClient();
        await Assert.ThrowsExactlyAsync<IOException>(() => new VerifiedDownloader(http).DownloadAsync(request));
        await Assert.ThrowsExactlyAsync<IOException>(() => VerifiedDownloader.IsValidAsync(request.DestinationPath, request));
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(target));
    }

    [TestMethod]
    public async Task ReplacingStagingDuringHttpAwaitCannotTruncateProtectedFixture()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Native Windows link mutation belongs to its provisioned native gate.");
        foreach (var suffix in new[] { ".part", ".part.resume.json" })
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var bytes = Encoding.UTF8.GetBytes("valid downloaded bytes");
            using var server = new LocalHttpFixture(context =>
            {
                received.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) { context.Response.StatusCode = 503; context.Response.Close(); return; }
                context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes); context.Response.Close();
            });
            var destination = Path.Combine(TestFiles.Root(), "client.jar");
            var protectedFile = Path.Combine(TestFiles.Root(), "independent-protected-fixture");
            await File.WriteAllTextAsync(protectedFile, "protected original bytes");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var download = new VerifiedDownloader(http).DownloadAsync(new([server.Uri("file")], destination,
                Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length));
            try
            {
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                File.CreateSymbolicLink(destination + suffix, protectedFile);
            }
            finally { release.Set(); }
            await Assert.ThrowsExactlyAsync<AggregateException>(() => download);
            Assert.AreEqual("protected original bytes", await File.ReadAllTextAsync(protectedFile));
            Assert.IsFalse(File.Exists(destination));
        }
    }

    private sealed class RecordingDownloader : IVerifiedDownloader
    {
        public int Calls;
        public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }

    // A real loopback HTTP/JSON-RPC exchange, with an explicitly synthetic daemon output.
    private sealed class RpcFixture : IDisposable
    {
        private readonly LocalHttpFixture server;
        public Uri Endpoint => server.Uri("jsonrpc");
        public string? Path { get; private set; }
        public int AddCount { get; private set; }
        public int ForgetCount { get; private set; }
        public JsonElement? Options;
        public int SourceCount;
        public RpcFixture(byte[] output)
        {
            server = new(context =>
            {
                using var body = JsonDocument.Parse(context.Request.InputStream);
                var root = body.RootElement; object response;
                var id = root.GetProperty("id").GetString();
                switch (root.GetProperty("method").GetString())
                {
                    case "aria2.tellStatus" when Path is null:
                        response = new { jsonrpc = "2.0", id, error = new { code = 1, message = "fixture GID missing" } }; break;
                    case "aria2.addUri":
                        var options = root.GetProperty("params")[1];
                        Options = options.Clone(); SourceCount = root.GetProperty("params")[0].GetArrayLength();
                        Path = System.IO.Path.Combine(options.GetProperty("dir").GetString()!, options.GetProperty("out").GetString()!);
                        File.WriteAllBytes(Path, output); AddCount++;
                        response = new { jsonrpc = "2.0", id, result = "fixture" }; break;
                    case "aria2.tellStatus":
                        response = new { jsonrpc = "2.0", id, result = new { status = "complete", files = new[] { new { path = Path } } } }; break;
                    case "aria2.removeDownloadResult":
                        ForgetCount++; response = new { jsonrpc = "2.0", id, result = "OK" }; break;
                    default:
                        response = new { jsonrpc = "2.0", id, error = new { code = -1, message = "unexpected fixture RPC" } }; break;
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes); context.Response.Close();
            });
        }
        public void Dispose() => server.Dispose();
    }
}
