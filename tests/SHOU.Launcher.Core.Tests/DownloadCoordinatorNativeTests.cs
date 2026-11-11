using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PCL.Core.Conduit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadCoordinatorNativeTests
{
    [TestMethod]
    public async Task RealDaemonUsesMirrorsVerifiesOutputAndPausesOnlyItsCancelledGid()
    {
        var executable = Environment.GetEnvironmentVariable("SHOU_TEST_ARIA2_PATH");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set SHOU_TEST_ARIA2_PATH to the provisioned local aria2c for the native transport gate.");
        Assert.IsTrue(File.Exists(executable));
        var bytes = new byte[16 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        using var server = new LocalHttpFixture(context =>
        {
            try
            {
                if (context.Request.Url!.AbsolutePath == "/missing") { context.Response.StatusCode = 404; context.Response.Close(); return; }
                var start = 0; var end = bytes.Length - 1;
                if (context.Request.Headers["Range"] is { } range)
                {
                    var positions = range[6..].Split('-'); start = int.Parse(positions[0]);
                    if (positions[1].Length > 0) end = Math.Min(end, int.Parse(positions[1]));
                    context.Response.StatusCode = 206; context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                }
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.ContentLength64 = end - start + 1;
                context.Response.OutputStream.Write(bytes, start, end - start + 1); context.Response.Close();
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { context.Response.Abort(); }
        });
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var info = new ProcessStartInfo(executable!) { UseShellExecute = false, WorkingDirectory = TestFiles.Root(), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var option in new[] { "--no-conf=true", "--enable-rpc=true", "--rpc-listen-all=false", "--disable-ipv6=true", $"--rpc-listen-port={port}", "--console-log-level=error", "--summary-interval=0", "--retry-wait=0" }) info.ArgumentList.Add(option);
        using var daemon = Process.Start(info)!; var stdout = daemon.StandardOutput.ReadToEndAsync(); var stderr = daemon.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var endpoint = new Uri($"http://127.0.0.1:{port}/jsonrpc"); var rpc = new Aria2RpcClient(http, endpoint, "");
            var ready = false;
            for (var i = 0; i < 60 && !ready; i++)
                try { _ = await rpc.GetVersionAsync(); ready = true; } catch (HttpRequestException) { await Task.Delay(50); }
            Assert.IsTrue(ready, "The local test daemon did not start.");
            var options = new NetworkOptions(); options.Update(new(Aria2: new(true, endpoint.AbsoluteUri), Downloads: new(MaximumConnections: 2)));
            var warnings = new List<string>();
            var downloader = new DownloadCoordinator(http, new VerifiedDownloader(http, options), options, warnings.Add);
            var destination = Path.Combine(TestFiles.Root(), "native.jar");
            var request = new DownloadRequest([server.Uri("missing"), server.Uri("file")], destination, Sha256: Convert.ToHexString(SHA256.HashData(bytes)), Size: bytes.Length);
            var reports = new List<TransferProgress>();
            await downloader.DownloadAsync(request, new ProgressCallback(reports.Add)).WaitAsync(TimeSpan.FromSeconds(30));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
            Assert.IsTrue(reports.Any(p => p.Stage == "Completed" && p.Backend == "aria2"), string.Join("; ", warnings) + "; daemonExited=" + daemon.HasExited + (daemon.HasExited ? "; exit=" + daemon.ExitCode + "; stdout=" + stdout.GetAwaiter().GetResult() + "; stderr=" + stderr.GetAwaiter().GetResult() : ""));
            Assert.IsFalse(reports.Any(p => p.Backend == "内置"));
            Assert.IsFalse(File.Exists(destination + ".aria2-part"));
            await rpc.CallAsync("changeGlobalOption", [new Dictionary<string, string> { ["max-overall-download-limit"] = "64K" }]);
            var cancelled = request with { DestinationPath = Path.Combine(TestFiles.Root(), "cancelled.jar"), Sources = [server.Uri("file")] };
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(cancelled, cancellationToken: cancel.Token));
            var state = await rpc.CallAsync("tellStatus", [Aria2RpcClient.TaskId(cancelled.DestinationPath + ".aria2-part")]);
            Assert.AreEqual("paused", (string?)state["status"]); Assert.IsFalse(File.Exists(cancelled.DestinationPath));
            await rpc.CallAsync("changeGlobalOption", [new Dictionary<string, string> { ["max-overall-download-limit"] = "0" }]);
            reports.Clear();
            await downloader.DownloadAsync(cancelled, new ProgressCallback(reports.Add)).WaitAsync(TimeSpan.FromSeconds(30));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(cancelled.DestinationPath));
            Assert.IsTrue(reports.Any(p => p.Stage == "Completed" && p.Backend == "aria2"), string.Join("; ", warnings) + "; daemonExited=" + daemon.HasExited + (daemon.HasExited ? "; exit=" + daemon.ExitCode + "; stdout=" + stdout.GetAwaiter().GetResult() + "; stderr=" + stderr.GetAwaiter().GetResult() : ""));
            Assert.IsFalse(reports.Any(p => p.Backend == "内置"));
        }
        finally
        {
            if (!daemon.HasExited) daemon.Kill(entireProcessTree: true);
            await daemon.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
        }
    }
    private sealed class ProgressCallback(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress progress) => report(progress); }
}
