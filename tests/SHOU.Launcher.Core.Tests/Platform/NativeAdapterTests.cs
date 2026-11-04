using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.MacOS;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class NativeAdapterTests
{
    [TestMethod]
    public async Task FactoryConstructionDoesNotCreateDataOrAccessTheStore()
    {
        var fixture = new PlatformFixture();
        var root = fixture.Child("factory untouched");
        var services = PlatformServicesFactory.Create(new PlatformOptions(root), new NeverDownloader());
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsTrue(services.Capabilities.SupportsCredentialPersistence);
        Assert.IsTrue(services.Capabilities.SupportsSystemProxy);
        await ((IAsyncDisposable)services.Processes).DisposeAsync();
    }

    [TestMethod]
    [TestCategory("NativeCredential")]
    public async Task NativeCredentialRoundTripFreshStoreInstanceAndIsolation()
    {
        var fixture = new PlatformFixture();
        var paths = new AppPaths(new PlatformOptions(fixture.Child("credentials")), new NativePlatformInfo().Context);
        var otherPaths = new AppPaths(new PlatformOptions(fixture.Child("other credentials")), new NativePlatformInfo().Context);
        using var lease = new DataDirectoryLock().Acquire(paths);
        using var otherLease = new DataDirectoryLock().Acquire(otherPaths);
        ICredentialStore Store(IAppPaths value)
        {
            if (OperatingSystem.IsMacOS()) return new KeychainCredentialStore(value.CredentialScope);
            if (OperatingSystem.IsWindows()) return new WindowsDpapiCredentialStore(value);
            throw new PlatformNotSupportedException("This native credential test requires Windows or macOS.");
        }
        var first = Store(paths);
        var other = Store(otherPaths);
        var key = "task-fixture-" + Guid.NewGuid().ToString("N");
        Assert.IsNull(await first.ReadAsync(key));
        var written = false;
        try
        {
            byte[] initial = Encoding.UTF8.GetBytes("fake token 中文 $literal");
            await first.WriteAsync(key, initial); written = true;
            CollectionAssert.AreEqual(initial, (await Store(paths).ReadAsync(key))!);
            Assert.IsNull(await other.ReadAsync(key));
            byte[] replacement = [0, 1, 2, 3, 255];
            await first.WriteAsync(key, replacement);
            CollectionAssert.AreEqual(replacement, (await Store(paths).ReadAsync(key))!);
            await first.DeleteAsync(key); written = false;
            Assert.IsNull(await first.ReadAsync(key));
        }
        finally { if (written) await first.DeleteAsync(key); }
    }

    [TestMethod]
    public async Task DirectAndExplicitProxyUseRealLocalHttpWithoutGlobalChanges()
    {
        using var direct = new OneRequestServer();
        using (var http = new HttpClient(PlatformProxy.CreateHandler(new ProxySettings(ProxyMode.Direct))))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.AreEqual("fixture-response", await http.GetStringAsync(direct.Uri, timeout.Token));
        }
        StringAssert.StartsWith(await direct.RequestLine, "GET /fixture");

        using var proxy = new OneRequestServer();
        var previous = HttpClient.DefaultProxy;
        using (var http = new HttpClient(PlatformProxy.CreateHandler(new ProxySettings(ProxyMode.Custom, proxy.Uri.GetLeftPart(UriPartial.Authority)))))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.AreEqual("fixture-response", await http.GetStringAsync("http://shou-proxy-fixture.invalid/test", timeout.Token));
        }
        StringAssert.StartsWith(await proxy.RequestLine, "GET http://shou-proxy-fixture.invalid/test");
        Assert.AreSame(previous, HttpClient.DefaultProxy);
        using var system = PlatformProxy.CreateHandler(new ProxySettings(ProxyMode.System));
        Assert.IsTrue(system.UseProxy);
        Assert.AreSame(previous, system.Proxy);
        Assert.Throws<ArgumentException>(() => PlatformProxy.CreateHandler(new ProxySettings(ProxyMode.Custom, "http://user:password@localhost:8888")));
    }

    [TestMethod]
    public async Task NativeOpenerRejectsUnsafeUriAndPreservesMacArgv()
    {
        var fixture = new PlatformFixture();
        var runner = new OpenFixtureRunner();
        var opener = new ExternalLauncher(runner, PlatformFixture.Context(LauncherOS.MacOS));
        await Assert.ThrowsAsync<ArgumentException>(() => opener.OpenUriAsync(new Uri("file:///etc/passwd")));
        await Assert.ThrowsAsync<ArgumentException>(() => opener.OpenUriAsync(new Uri("https://name:password@example.invalid/")));
        if (OperatingSystem.IsMacOS())
        {
            await opener.OpenDirectoryAsync(fixture.Root);
            Assert.AreEqual("/usr/bin/open", runner.LastSpec!.FileName);
            CollectionAssert.AreEqual(new[] { "--", fixture.Root }, runner.LastSpec.Arguments.ToArray());
            await opener.RevealFileAsync(fixture.JavaSource);
            CollectionAssert.AreEqual(new[] { "-R", "--", fixture.JavaSource }, runner.LastSpec.Arguments.ToArray());
        }
        else await Assert.ThrowsAsync<PlatformNotSupportedException>(() => opener.OpenDirectoryAsync(fixture.Root));
    }

    private sealed class NeverDownloader : IVerifiedDownloader
    {
        public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Factory construction must not perform downloads.");
    }
    private sealed class OpenFixtureRunner : IProcessRunner
    {
        public ProcessSpec? LastSpec { get; private set; }
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Explicit opener unit fixture only acknowledges requests.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
        { LastSpec = spec; return Task.FromResult(new ProcessResult(0, "", "")); }
    }
    private sealed class OneRequestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public Uri Uri { get; }
        public Task<string> RequestLine { get; }
        public OneRequestServer()
        {
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/fixture");
            RequestLine = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing HTTP request");
                var count = 0;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                    if (++count > 100) throw new IOException("Too many request headers");
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\nfixture-response"u8.ToArray();
                await stream.WriteAsync(response, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                return first;
            });
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop();
            try { RequestLine.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            _stop.Dispose();
        }
    }
}
