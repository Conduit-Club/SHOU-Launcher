using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class JavaTrustWorkflowTests
{
    [TestMethod]
    public async Task PreparationIsSeparateFromDurableSelectionAndAutomaticRollback()
    {
        await using var f = await Fixture.CreateAsync(); var before = f.Service.Snapshot.Settings;
        var prepared = await f.Service.PrepareJavaTrustCopyAsync(f.Discovery.Source.ExecutablePath);
        Assert.AreEqual(before, f.Service.Snapshot.Settings); Assert.IsFalse(File.Exists(f.Config));
        var change = await f.Service.SelectJavaTrustCopyAsync(prepared);
        Assert.IsNull(change.PreviousJavaExecutable); Assert.AreEqual(prepared.Java.ExecutablePath, f.Service.Snapshot.Settings.JavaExecutable);
        Assert.AreEqual(2, f.Trust.Calls); Assert.IsTrue(File.Exists(f.Config));
        await using var store = new SHOU.Launcher.Core.Configuration.ConfigurationStore(f.Service.Paths);
        Assert.AreEqual(change, (await store.LoadAsync()).Settings.JavaTrustSelection);
        await f.Service.RestoreJavaTrustSelectionAsync(change);
        Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable); Assert.IsTrue(f.Service.Snapshot.JavaInstallations.Any(j => j.ExecutablePath == prepared.Java.ExecutablePath));
        Assert.IsNull((await store.LoadAsync()).Settings.JavaTrustSelection);
    }
    [TestMethod]
    public async Task ChangedReceiptOrOutputCannotBecomeTheDefaultJava()
    {
        await using var f = await Fixture.CreateAsync(); var prepared = await f.Service.PrepareJavaTrustCopyAsync(f.Discovery.Source.ExecutablePath);
        f.Trust.ChangeOutputHash = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Service.SelectJavaTrustCopyAsync(prepared));
        Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable); Assert.IsFalse(File.Exists(f.Config));
    }
    [TestMethod]
    public async Task SelectionCancellationAndUnwritableSettingsKeepThePreviousDefault()
    {
        await using var f = await Fixture.CreateAsync(); var prepared = await f.Service.PrepareJavaTrustCopyAsync(f.Discovery.Source.ExecutablePath);
        f.Trust.DelayReuse = true; using var cancellation = new CancellationTokenSource();
        var pending = f.Service.SelectJavaTrustCopyAsync(prepared, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending); Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable);
        f.Trust.DelayReuse = false; Directory.CreateDirectory(f.Config);
        await Assert.ThrowsAsync<Exception>(() => f.Service.SelectJavaTrustCopyAsync(prepared));
        Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable);
    }
    [TestMethod]
    public async Task RollbackProbesThePriorChoiceAndDoesNotOverwriteANewerSelection()
    {
        await using var f = await Fixture.CreateAsync();
        var prior = f.Discovery.Source.ExecutablePath; await f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings with { JavaExecutable = prior });
        var prepared = await f.Service.PrepareJavaTrustCopyAsync(prior); var change = await f.Service.SelectJavaTrustCopyAsync(prepared);
        Assert.AreEqual(prior, change.PreviousJavaExecutable);
        f.Discovery.FailSource = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Service.RestoreJavaTrustSelectionAsync(change));
        Assert.AreEqual(prepared.Java.ExecutablePath, f.Service.Snapshot.Settings.JavaExecutable);
        f.Discovery.FailSource = false; await f.Service.RestoreJavaTrustSelectionAsync(change);
        Assert.AreEqual(prior, f.Service.Snapshot.Settings.JavaExecutable);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.RestoreJavaTrustSelectionAsync(change));
        Assert.AreEqual(prior, f.Service.Snapshot.Settings.JavaExecutable);
    }
    [TestMethod]
    public async Task ArbitraryExternalRuntimeCannotMasqueradeAsAPreparedCopy()
    {
        await using var f = await Fixture.CreateAsync(); var prepared = await f.Service.PrepareJavaTrustCopyAsync(f.Discovery.Source.ExecutablePath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Service.SelectJavaTrustCopyAsync(prepared with { Java = f.Discovery.Source }));
        Assert.IsNull(f.Service.Snapshot.Settings.JavaExecutable);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required LauncherService Service;
        public required Discovery Discovery;
        public required Trust Trust;
        public required IDisposable Lease;
        public required HttpClient Http;
        public required ProcessRunner Processes;
        public string Config => Path.Combine(Service.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        public static async Task<Fixture> CreateAsync()
        {
            var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Workflow fixture must not use a network.")));
            var downloader = new VerifiedDownloader(http); var native = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
            var sourceHome = TestFiles.Root();
            var discovery = new Discovery(new(Path.Combine(sourceHome, "bin/java"), sourceHome, "21.0.13", 21, "fixture", native.Info.Context.HardwareArchitecture, "fixture:source"));
            var trust = new Trust(discovery); var platform = native with { JavaDiscovery = discovery }; var lease = platform.DataLock.Acquire(platform.Paths);
            var service = new LauncherService(platform, http, downloader, new(), javaTrust: trust); await service.InitializeAsync();
            return new() { Service = service, Discovery = discovery, Trust = trust, Lease = lease, Http = http, Processes = (ProcessRunner)native.Processes };
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Processes.DisposeAsync(); Lease.Dispose(); Http.Dispose(); }
    }
    private sealed class Discovery(JavaInstallation source) : IJavaDiscovery
    {
        public JavaInstallation Source = source;
        public JavaInstallation? Variant;
        public bool FailSource;
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Variant?.ExecutablePath == executable) return Task.FromResult(Variant);
            if (FailSource) throw new IOException("Source no longer probes");
            return Task.FromResult(Source with { ExecutablePath = executable });
        }
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JavaInstallation>>([Source]);
    }
    private sealed class Trust(Discovery discovery) : IJavaTrustService
    {
        public int Calls;
        public bool ChangeOutputHash, DelayReuse;
        private JavaTrustDerivation? completed;
        public Task<IReadOnlyList<JavaTrustFinding>> InspectAsync(JavaInstallation java, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JavaTrustFinding>>([]);
        public async Task<JavaTrustDerivation> CreateRepairedCopyAsync(JavaTrustCopyRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (completed is not null && request.Source.ExecutablePath == completed.Java.ExecutablePath)
            {
                if (DelayReuse) await Task.Delay(Timeout.Infinite, ct);
                return ChangeOutputHash ? completed with { OutputTreeSha256 = new string('c', 64) } : completed;
            }
            var home = Path.Combine(request.DerivedRoot, "fixture-variant", "home");
            discovery.Variant = request.Source with { JavaHome = home, ExecutablePath = Path.Combine(home, "bin/java"), Source = "managed:derived" };
            return completed = new(discovery.Variant, request.Source.ExecutablePath, "fixture-policy", new string('a', 64), new string('b', 64), Path.Combine(home, "../provenance.json"), []);
        }
    }
}
