using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class Aria2CdnNativeTests
{
    [TestMethod]
    public async Task ExternalRpcTransfersAllowlistedCdnAndBlockedRedirectCannotOverwriteExistingFile()
    {
        await using var daemon = await ExternalDaemon.StartAsync();
        var originalVersion = await daemon.Rpc.GetVersionAsync(); Assert.IsFalse(string.IsNullOrWhiteSpace(originalVersion));
        var bytes = new byte[128 * 1024]; RandomNumberGenerator.Fill(bytes);
        var blocked = false; var leaked = false; var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            calls++; leaked |= request.Headers.Contains("x-api-key");
            Assert.AreEqual("edge.forgecdn.net", request.RequestUri!.Host);
            return Task.FromResult(blocked ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://untrusted.invalid/file") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        var options = daemon.Options; options.Update(new(Aria2: new(true, daemon.Endpoint.AbsoluteUri), Downloads: new(HttpsOnly: true, MaximumKiBPerSecond: 1024)));
        var resolver = new CurseForgePackResolver(http, "synthetic-cf-key", network: options);
        var file = new ContentFile("pack.zip", new("https://edge.forgecdn.net/files/1234/567/pack.zip"), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true);
        var path = Path.Combine(TestFiles.Root(), "pack.zip"); var progress = new List<TransferProgress>();
        await resolver.DownloadResourceAsync(file, path, new Callback(progress.Add));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(progress.Any(p => p.Backend == "aria2" && p.Stage == "Completed"));
        Assert.IsTrue(progress.All(p => p.Source == "edge.forgecdn.net")); Assert.IsFalse(leaked);
        blocked = true; await File.WriteAllTextAsync(path, "original user bytes");
        await Assert.ThrowsAsync<IOException>(() => resolver.DownloadResourceAsync(file, path));
        Assert.AreEqual("original user bytes", await File.ReadAllTextAsync(path)); Assert.IsTrue(calls > 1);
        Assert.AreEqual(originalVersion, await daemon.Rpc.GetVersionAsync());
    }

    [TestMethod]
    public async Task CdnCancellationAndNewRelayResumeKeepExternalDaemonAlive()
    {
        await using var daemon = await ExternalDaemon.StartAsync();
        var originalVersion = await daemon.Rpc.GetVersionAsync(); Assert.IsFalse(string.IsNullOrWhiteSpace(originalVersion));
        var bytes = new byte[2 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            var range = request.Headers.Range?.Ranges.Single(); var start = (int)(range?.From ?? 0); var end = (int)(range?.To ?? bytes.Length - 1);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[start..(end + 1)]) };
            if (range is not null) response.Content.Headers.ContentRange = new(start, end, bytes.Length);
            return Task.FromResult(response);
        }));
        var options = daemon.Options; options.Update(new(Aria2: new(true, daemon.Endpoint.AbsoluteUri), Downloads: new(MaximumKiBPerSecond: 256)));
        var resolver = new CurseForgePackResolver(http, null, network: options);
        var file = new ContentFile("pack.zip", new("https://edge.forgecdn.net/files/1234/567/pack.zip"), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true);
        var path = Path.Combine(TestFiles.Root(), "pack.zip");
        using (var cancel = new CancellationTokenSource(750)) await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.DownloadResourceAsync(file, path, ct: cancel.Token));
        Assert.IsFalse(File.Exists(path));
        options.Update(new(Aria2: new(true, daemon.Endpoint.AbsoluteUri)));
        var progress = new List<TransferProgress>();
        await resolver.DownloadResourceAsync(file, path, new Callback(progress.Add)).WaitAsync(TimeSpan.FromSeconds(30));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.IsTrue(progress.Any(p => p.Stage == "Completed" && p.Backend == "aria2"));
        options.Update(new(Aria2: new(false, daemon.Endpoint.AbsoluteUri)));
        Assert.AreEqual(originalVersion, await daemon.Rpc.GetVersionAsync());
    }

    private sealed class Callback(Action<TransferProgress> report) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => report(value); }

    // Test-owned stand-in for a daemon started by the user/GUI; product code never launches it.
    private sealed class ExternalDaemon(Process process, HttpClient http, Uri endpoint, Task stdout, Task stderr) : IAsyncDisposable
    {
        public Uri Endpoint => endpoint;
        public Aria2RpcClient Rpc { get; } = new(http, endpoint, "");
        public NetworkOptions Options { get; } = new();
        public static async Task<ExternalDaemon> StartAsync()
        {
            var executable = Environment.GetEnvironmentVariable("SHOU_TEST_ARIA2_PATH");
            if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Provision SHOU_TEST_ARIA2_PATH for the native external-RPC gate.");
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            var info = new ProcessStartInfo(executable!) { UseShellExecute = false, WorkingDirectory = TestFiles.Root(), RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--no-conf=true", "--enable-rpc=true", "--rpc-listen-all=false", "--disable-ipv6=true", $"--rpc-listen-port={port}", "--console-log-level=error", "--summary-interval=0", "--retry-wait=0" }) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            var fixture = new ExternalDaemon(process, new HttpClient(new HttpClientHandler { UseProxy = false }), new($"http://127.0.0.1:{port}/jsonrpc"), process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            try
            {
                for (var i = 0; i < 60; i++)
                { try { _ = await fixture.Rpc.GetVersionAsync(); return fixture; } catch (HttpRequestException) { await Task.Delay(50); } }
                throw new IOException("External RPC test fixture did not become ready.");
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); process.Dispose(); http.Dispose();
        }
    }
}
