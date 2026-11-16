using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Reports(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }
    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 256 });
    private static NetworkOptions Options(int connections)
    { var options = new NetworkOptions(); options.Update(new(Downloads: new(MaximumConnections: connections))); return options; }

    [TestMethod]
    public async Task ManySmallFilesUseTheConfiguredSlotsInsteadOfFourOrEight()
    {
        var payload = new byte[16384]; Random.Shared.NextBytes(payload);
        var results = new List<(int Connections, long Ms, int Peak)>();
        foreach (var connections in new[] { 8, 64, DownloadDefaults.Connections })
        {
            using var server = new RangeServer(payload, delayMs: 60); using var http = Client();
            var downloader = new VerifiedDownloader(http, Options(connections)); var root = TestFiles.Root();
            var timer = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, 256), new ParallelOptions { MaxDegreeOfParallelism = connections },
                async (i, ct) => await downloader.DownloadAsync(new([server.Uri($"asset/{i}")], Path.Combine(root, i + ".bin"), Sha256: Hash(payload), Size: payload.Length), cancellationToken: ct));
            results.Add((connections, timer.ElapsedMilliseconds, server.Peak));
            Assert.IsTrue(server.Peak <= connections, $"Shared budget exceeded: {server.Peak}/{connections}.");
            Assert.AreEqual(256, Directory.GetFiles(root, "*.bin").Length);
        }
        Assert.IsTrue(results[^1].Peak > 64, $"New default never exceeded the old limit: {results[^1].Peak}.");
        TestContext.WriteLine("DOWNLOAD_PERF " + JsonSerializer.Serialize(results.Select(r => new { connections = r.Connections, elapsed_ms = r.Ms, peak = r.Peak })));
    }

    [TestMethod]
    public async Task LargeFileUsesEightRealHttpRangesAndFinishesWithoutAPostDownloadScan()
    {
        var payload = new byte[12 * 1024 * 1024]; Random.Shared.NextBytes(payload);
        using var server = new RangeServer(payload); using var http = Client();
        var reports = new ConcurrentQueue<TransferProgress>(); var path = Path.Combine(TestFiles.Root(), "client.jar");
        await new VerifiedDownloader(http, Options(64)).DownloadAsync(new([server.Uri("client")], path,
            Sha1: Convert.ToHexStringLower(SHA1.HashData(payload)), Sha256: Hash(payload), Size: payload.Length), new Reports(reports.Enqueue));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(server.Peak > 1 && server.Peak <= 8); Assert.IsTrue(server.Ranges.Count > 8);
        Assert.IsTrue(reports.Any(r => r.Stage == "Downloading" && r.Connections > 1));
        Assert.IsFalse(reports.Any(r => r.Stage == "Verifying")); Assert.AreEqual("Completed", reports.Last().Stage);
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task ServersIgnoringRangesFallBackWithoutDuplicatingBytes(bool ignoreOnlyChunks)
    {
        var payload = new byte[9 * 1024 * 1024]; Random.Shared.NextBytes(payload);
        using var server = new RangeServer(payload, ignoreRanges: true, probeWorks: ignoreOnlyChunks); using var http = Client();
        var path = Path.Combine(TestFiles.Root(), "client.jar");
        await new VerifiedDownloader(http, Options(64)).DownloadAsync(new([server.Uri("client")], path, Sha256: Hash(payload), Size: payload.Length));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(server.WholeRequests > 0);
    }

    [TestMethod]
    public async Task CancellationKeepsAnOrderedPrefixAndResumesItWithTheSameFinalHash()
    {
        var payload = new byte[16 * 1024 * 1024]; Random.Shared.NextBytes(payload);
        using var server = new RangeServer(payload, delayMs: 100); using var http = Client(); using var cancel = new CancellationTokenSource();
        var path = Path.Combine(TestFiles.Root(), "client.jar"); await File.WriteAllTextAsync(path, "previous installed file");
        var request = new DownloadRequest([server.Uri("client")], path, Sha256: Hash(payload), Size: payload.Length);
        var downloader = new VerifiedDownloader(http, Options(64));
        await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(request, new Reports(p =>
        {
            if (p.Stage == "Downloading" && new FileInfo(path + ".part").Length >= 1024 * 1024) cancel.Cancel();
        }), cancel.Token));
        Assert.AreEqual("previous installed file", await File.ReadAllTextAsync(path));
        var prefix = new FileInfo(path + ".part").Length; Assert.IsTrue(prefix > 0 && prefix < payload.Length);
        var oldRanges = server.Ranges.Count;
        await downloader.DownloadAsync(request);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(server.Ranges.Skip(oldRanges).Any(r => r.From == prefix));
    }

    [TestMethod]
    public async Task CorruptParallelOutputNeverReplacesAnInstalledFileAndReleasesEverySlot()
    {
        var payload = new byte[8 * 1024 * 1024]; using var server = new RangeServer(payload); using var http = Client();
        var path = Path.Combine(TestFiles.Root(), "client.jar"); await File.WriteAllTextAsync(path, "keep this"); var options = Options(8);
        await Assert.ThrowsAsync<AggregateException>(() => new VerifiedDownloader(http, options).DownloadAsync(new([server.Uri("client")], path, Sha256: new string('a', 64), Size: payload.Length)));
        Assert.AreEqual("keep this", await File.ReadAllTextAsync(path));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var all = await options.Budget.EnterAsync(8, 8, deadline.Token);
    }

    [TestMethod]
    public async Task MultipleLargeFilesShareSlotsWithoutHoldingPartialReservations()
    {
        var payload = new byte[8 * 1024 * 1024]; using var server = new RangeServer(payload); using var http = Client();
        var root = TestFiles.Root(); var downloader = new VerifiedDownloader(http, Options(8)); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => downloader.DownloadAsync(new([server.Uri("client/" + i)], Path.Combine(root, i + ".jar"), Sha256: Hash(payload), Size: payload.Length), cancellationToken: deadline.Token)));
        Assert.IsTrue(server.Peak <= 8); Assert.AreEqual(3, Directory.GetFiles(root, "*.jar").Length);
    }

    [TestMethod]
    public async Task OldDefaultUpgradesOnceAndExplicitConcurrencyPersists()
    {
        var paths = new AppPaths(new(TestFiles.Root()), new NativePlatformInfo().Context); Directory.CreateDirectory(paths.LocalDataDirectory);
        var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
        await File.WriteAllTextAsync(file, """{"schemaVersion":1,"settings":{"downloads":{"maximumConnections":8}},"instances":[],"accounts":[]}""");
        await using (var store = new ConfigurationStore(paths))
        {
            var state = await store.LoadAsync(); Assert.AreEqual(256, state.Settings.Downloads!.MaximumConnections);
            await store.SaveAsync(state with { Settings = state.Settings with { Downloads = new(MaximumConnections: 8) } });
        }
        await using var reopened = new ConfigurationStore(paths); Assert.AreEqual(8, (await reopened.LoadAsync()).Settings.Downloads!.MaximumConnections);
        ConfigurationStore.ValidateSettings(new(Downloads: new(MaximumConnections: 256)));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationStore.ValidateSettings(new(Downloads: new(MaximumConnections: 257))));
    }

    [TestMethod]
    public async Task MinecraftLibrariesWithoutMetadataHashDoNotFetchSidecarsOrDownloadAgain()
    {
        var root = TestFiles.Root(); var calls = new ConcurrentBag<string>();
        byte[] client = "client"u8.ToArray(), library = "library"u8.ToArray(), index = "{\"objects\":{}}"u8.ToArray();
        JsonObject Descriptor(byte[] bytes, string path) => new() { ["url"] = "https://example.invalid/" + path, ["size"] = (long)bytes.Length, ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(bytes)) };
        var metadata = new JsonObject { ["id"] = "fixture", ["mainClass"] = "net.minecraft.client.main.Main",
            ["downloads"] = new JsonObject { ["client"] = Descriptor(client, "client") }, ["assetIndex"] = Descriptor(index, "index"),
            ["libraries"] = new JsonArray(new JsonObject { ["name"] = "fixture:library:1", ["url"] = "https://example.invalid/" }) };
        metadata["assetIndex"]!["id"] = "fixture";
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath; calls.Add(path);
            Assert.IsFalse(path.EndsWith(".sha1"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(path == "/client" ? client : path == "/index" ? index : library) });
        }));
        var installer = new MinecraftInstaller(new VerifiedDownloader(http), Path.Combine(root, "cache"), () => DownloadSources.Defaults());
        var version = new ResolvedVersion("fixture", metadata, Path.Combine(root, "fixture.json"), ["fixture"]);
        await installer.InstallAsync(version, new("windows", "10", RuntimeArchitecture.X64), Path.Combine(root, "game"));
        Assert.AreEqual(3, calls.Count); calls.Clear();
        await installer.InstallAsync(version, new("windows", "10", RuntimeArchitecture.X64), Path.Combine(root, "game"));
        Assert.AreEqual(0, calls.Count);
    }

    private sealed class RangeServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> requests = [];
        private readonly Task loop;
        private readonly int port;
        private int active, peak, whole;
        public int Peak => Volatile.Read(ref peak);
        public int WholeRequests => Volatile.Read(ref whole);
        public ConcurrentQueue<(long From, long To)> Ranges { get; } = [];
        public RangeServer(byte[] payload, int delayMs = 30, bool ignoreRanges = false, bool probeWorks = false)
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            async Task Respond(HttpListenerContext context)
            {
                var count = Interlocked.Increment(ref active);
                while (count > Volatile.Read(ref peak)) { var old = Volatile.Read(ref peak); if (count <= old || Interlocked.CompareExchange(ref peak, count, old) == old) break; }
                try
                {
                    await Task.Delay(delayMs, stop.Token);
                    var range = context.Request.Headers["Range"]; long from = 0, to = payload.Length - 1;
                    if (range is not null && (!ignoreRanges || probeWorks && range == "bytes=0-0"))
                    {
                        var parts = range[6..].Split('-'); from = long.Parse(parts[0]); to = parts[1].Length == 0 ? to : long.Parse(parts[1]);
                        Ranges.Enqueue((from, to)); context.Response.StatusCode = 206;
                        context.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{payload.Length}";
                    }
                    else Interlocked.Increment(ref whole);
                    context.Response.Headers["ETag"] = "\"fixture-v1\"";
                    context.Response.ContentLength64 = to - from + 1;
                    await context.Response.OutputStream.WriteAsync(payload.AsMemory((int)from, (int)(to - from + 1)), stop.Token);
                }
                catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
                finally { context.Response.Close(); Interlocked.Decrement(ref active); }
            }
            loop = Task.Run(async () =>
            {
                try { while (!stop.IsCancellationRequested) requests.Add(Respond(await listener.GetContextAsync().WaitAsync(stop.Token))); }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });
        }
        public Uri Uri(string path) => new($"http://127.0.0.1:{port}/" + path);
        public void Dispose() { stop.Cancel(); listener.Close(); loop.GetAwaiter().GetResult(); Task.WhenAll(requests).GetAwaiter().GetResult(); stop.Dispose(); }
    }
}
