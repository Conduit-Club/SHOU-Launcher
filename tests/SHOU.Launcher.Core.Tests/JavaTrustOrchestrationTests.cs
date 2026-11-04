using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

// Explicit service-protocol fixtures. Real copy/keytool/probe belongs to the Java worker suite.
[TestClass, TestCategory("DesktopPlatforms")]
public sealed class JavaTrustOrchestrationTests
{
    [TestMethod]
    public async Task InspectionDoesNotChangeSelectionAndFailureCannotPublishDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Service.Snapshot.Settings;
        var findings = await fixture.Service.InspectJavaTrustAsync(fixture.Java.Source.ExecutablePath);
        Assert.HasCount(1, findings); Assert.IsTrue(findings[0].NeedsRepair);
        Assert.AreEqual(before, fixture.Service.Snapshot.Settings);
        fixture.Trust.Failure = new IOException("explicit fixture derivation failure");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath));
        Assert.AreEqual(before, fixture.Service.Snapshot.Settings);
        Assert.IsFalse(File.Exists(fixture.ConfigFile));
    }

    [TestMethod]
    public async Task CopyPublishesSelectionOnlyAfterValidatedResultAndDurableWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Java.Source.ExecutablePath;
        var result = await fixture.Service.CreateRepairedJavaCopyAsync(original);
        Assert.AreEqual(original, result.SourceJavaExecutable);
        Assert.AreNotEqual(original, result.Java.ExecutablePath);
        Assert.AreEqual(result.Java.ExecutablePath, fixture.Service.Snapshot.Settings.JavaExecutable);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigFile))!;
        Assert.AreEqual(result.Java.ExecutablePath, (string?)saved["settings"]?["javaExecutable"]);
        Assert.AreEqual(original, fixture.Trust.Request!.Source.ExecutablePath);
        Assert.AreEqual(Path.Combine(fixture.Service.Paths.RuntimesDirectory, "derived-trust"), fixture.Trust.Request.DerivedRoot);
    }

    [TestMethod]
    public async Task FailedSettingsWriteLeavesOriginalSelectionAfterSuccessfulCopy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.Service.Snapshot.Settings;
        Directory.CreateDirectory(fixture.ConfigFile);
        await Assert.ThrowsAsync<Exception>(() => fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath));
        Assert.AreEqual(1, fixture.Trust.Calls);
        Assert.AreEqual(before, fixture.Service.Snapshot.Settings);
    }

    [TestMethod]
    public async Task UnwritableObservationalLogCannotFailAnAlreadyCommittedCopySelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var log = Path.Combine(fixture.Service.Paths.LogsDirectory, "launcher.log");
        if (File.Exists(log)) File.Move(log, log + ".fixture-preserved");
        Directory.CreateDirectory(log);
        var result = await fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath);
        Assert.AreEqual(result.Java.ExecutablePath, fixture.Service.Snapshot.Settings.JavaExecutable);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigFile))!;
        Assert.AreEqual(result.Java.ExecutablePath, (string?)saved["settings"]?["javaExecutable"]);
        Assert.AreNotEqual("Error", fixture.Service.Snapshot.State);
    }

    [TestMethod]
    public async Task ObservationalLogLinkCannotOverwriteAnIndependentFixture()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Native Windows reparse fixtures are a separate gate.");
        await using var fixture = await Fixture.CreateAsync();
        var log = Path.Combine(fixture.Service.Paths.LogsDirectory, "launcher.log");
        if (File.Exists(log)) File.Move(log, log + ".fixture-preserved");
        var protectedFile = Path.Combine(TestFiles.Root(), "protected-log-fixture");
        await File.WriteAllTextAsync(protectedFile, "independent protected bytes");
        File.CreateSymbolicLink(log, protectedFile);
        var result = await fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath);
        Assert.AreEqual(result.Java.ExecutablePath, fixture.Service.Snapshot.Settings.JavaExecutable);
        Assert.AreEqual("independent protected bytes", await File.ReadAllTextAsync(protectedFile));
    }

    [TestMethod]
    [DataRow("major")]
    [DataRow("architecture")]
    [DataRow("outside")]
    public async Task InvalidDerivationCannotBecomeSelected(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Trust.Transform = java => kind switch
        {
            "major" => java with { MajorVersion = 25 },
            "architecture" => java with { RuntimeArchitecture = java.RuntimeArchitecture == RuntimeArchitecture.X64 ? RuntimeArchitecture.Arm64 : RuntimeArchitecture.X64 },
            _ => java with { JavaHome = TestFiles.Root(), ExecutablePath = Path.Combine(TestFiles.Root(), "bin/java") }
        };
        var before = fixture.Service.Snapshot.Settings;
        if (kind == "outside")
            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Service.CreateRepairedJavaCopyAsync(fixture.Java.Source.ExecutablePath));
        Assert.AreEqual(before, fixture.Service.Snapshot.Settings);
        Assert.IsFalse(File.Exists(fixture.ConfigFile));
    }

    [TestMethod]
    public async Task OfficialInstallDerivationCompletesBeforeGameDownloadsWithoutHiddenInstanceOverride()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Java.DiscoverNothing = true;
        fixture.Downloads.BeforeTransfer = () => Assert.AreEqual(1, fixture.Trust.Calls);
        var instance = await fixture.Service.CreateInstanceAsync("Trust sequencing", "fixture");
        await fixture.Service.InstallInstanceAsync(instance.Id);
        Assert.AreEqual(1, fixture.Installer.Calls); Assert.AreEqual(1, fixture.Trust.Calls);
        Assert.AreEqual(new string('1', 40), fixture.Trust.Request!.SourceManifestSha1);
        var selected = fixture.Service.Snapshot.Instances.Single();
        Assert.IsTrue(selected.Installed);
        Assert.IsNull(selected.JavaExecutable, "Automatic managed selection must not create an invisible permanent instance override.");
        Assert.IsTrue(fixture.Service.Snapshot.JavaInstallations.Any(java => java.ExecutablePath.Contains("derived-trust", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GameDownloadFailureAfterDerivationCannotChangeInstanceJavaOverride()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Java.DiscoverNothing = true;
        fixture.Downloads.BeforeTransfer = () => throw new IOException("explicit fixture game download failure");
        var instance = await fixture.Service.CreateInstanceAsync("Failed after copy", "fixture");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.InstallInstanceAsync(instance.Id));
        Assert.AreEqual(1, fixture.Trust.Calls);
        var actual = fixture.Service.Snapshot.Instances.Single();
        Assert.IsFalse(actual.Installed); Assert.IsNull(actual.JavaExecutable);
    }

    [TestMethod]
    public async Task DisabledAutoRepairPreservesOfficialInstallAndSavedPreference()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Java.DiscoverNothing = true;
        await fixture.Service.SaveSettingsAsync(fixture.Service.Snapshot.Settings with { ConduitJavaTrustAutoRepair = false });
        var instance = await fixture.Service.CreateInstanceAsync("Auto disabled", "fixture");
        await fixture.Service.InstallInstanceAsync(instance.Id);
        Assert.AreEqual(1, fixture.Installer.Calls); Assert.AreEqual(0, fixture.Trust.Calls);
        Assert.IsTrue(fixture.Service.Snapshot.Instances.Single().Installed);
        Assert.IsFalse(fixture.Service.Snapshot.Settings.ConduitJavaTrustAutoRepair);
    }

    [TestMethod]
    public async Task DerivedRuntimeStillRequiresProvenanceWhenAutoRepairIsDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var home = Path.Combine(fixture.Service.Paths.RuntimesDirectory, "derived-trust", "fixture-variant", "home");
        fixture.Java.Source = fixture.Java.Source with { JavaHome = home, ExecutablePath = Path.Combine(home, "bin/java") };
        await fixture.Service.SaveSettingsAsync(fixture.Service.Snapshot.Settings with { JavaExecutable = fixture.Java.Source.ExecutablePath, ConduitJavaTrustAutoRepair = false });
        fixture.Trust.Failure = new IOException("explicit fixture provenance mismatch");
        var instance = await fixture.Service.CreateInstanceAsync("Reject tampered variant", "fixture");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.InstallInstanceAsync(instance.Id));
        Assert.AreEqual(1, fixture.Trust.Calls); Assert.AreEqual(0, fixture.Downloads.Calls);
        Assert.IsFalse(fixture.Service.Snapshot.Instances.Single().Installed);
    }

    [TestMethod]
    public async Task LiteralDerivedExecutableWithExternalJavaHomeCannotBypassProvenanceGate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inside = Path.Combine(fixture.Service.Paths.RuntimesDirectory, "derived-trust", "fixture-variant/home/bin/java");
        fixture.Java.Source = fixture.Java.Source with { ExecutablePath = inside, JavaHome = TestFiles.Root() };
        await fixture.Service.SaveSettingsAsync(fixture.Service.Snapshot.Settings with { JavaExecutable = inside, ConduitJavaTrustAutoRepair = false });
        var instance = await fixture.Service.CreateInstanceAsync("Escaping probe home", "fixture");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Service.InstallInstanceAsync(instance.Id));
        Assert.AreEqual(0, fixture.Downloads.Calls);
        Assert.IsFalse(fixture.Service.Snapshot.Instances.Single().Installed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required LauncherService Service;
        public required ProcessRunner Processes;
        public required IDataDirectoryLease Lease;
        public required HttpClient Http;
        public required JavaFixture Java;
        public required InstallerFixture Installer;
        public required TrustFixture Trust;
        public required PipelineDownloads Downloads;
        public string ConfigFile => Path.Combine(Service.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        public static async Task<Fixture> CreateAsync()
        {
            var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Cached service fixture must not use the network.")));
            var downloads = new PipelineDownloads();
            var native = PlatformServicesFactory.Create(new(TestFiles.Root()), downloads);
            var java = new JavaFixture(native.Info.Context.HardwareArchitecture);
            var installer = new InstallerFixture(java); var trust = new TrustFixture();
            var platform = native with { JavaDiscovery = java, JavaInstaller = installer };
            var lease = platform.DataLock.Acquire(platform.Paths);
            var service = new LauncherService(platform, http, downloads, new NetworkOptions(), javaTrust: trust);
            await service.InitializeAsync();
            var metadata = Path.Combine(platform.Paths.CacheDirectory, "versions/fixture/fixture.json");
            Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
            await File.WriteAllTextAsync(metadata, $$$"""
                {"id":"fixture","type":"release","mainClass":"fixture.Main","javaVersion":{"component":"java-runtime-delta","majorVersion":21},"libraries":[],
                "downloads":{"client":{"url":"https://example.invalid/client","sha1":"{{{PipelineDownloads.Hash(PipelineDownloads.Client)}}}","size":{{{PipelineDownloads.Client.Length}}}}},
                "assetIndex":{"id":"fixture-assets","url":"https://example.invalid/assets","sha1":"{{{PipelineDownloads.Hash(PipelineDownloads.Assets)}}}","size":{{{PipelineDownloads.Assets.Length}}}},
                "arguments":{"jvm":["-cp","${classpath}"],"game":[]}}
                """);
            return new() { Service = service, Processes = (ProcessRunner)native.Processes, Lease = lease, Http = http,
                Java = java, Installer = installer, Trust = trust, Downloads = downloads };
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); await Processes.DisposeAsync(); Lease.Dispose(); Http.Dispose(); }
    }
    private sealed class JavaFixture(RuntimeArchitecture architecture) : IJavaDiscovery
    {
        public JavaInstallation Source = CreateJava(architecture);
        private static JavaInstallation CreateJava(RuntimeArchitecture architecture)
        {
            var home = TestFiles.Root();
            return new(Path.Combine(home, "bin/java"), home, "21.0.13", 21, "explicit fixture", architecture, "fixture");
        }
        public bool DiscoverNothing;
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(Source with { ExecutablePath = executable });
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaInstallation>>(DiscoverNothing ? [] : [Source]);
    }
    private sealed class InstallerFixture(JavaFixture java) : IJavaRuntimeInstaller
    {
        public int Calls;
        public Task<JavaInstallation> InstallAsync(JavaRuntimeRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(java.Source with { Source = "managed:Mojang", ValidatedManifestSha1 = new string('1', 40) }); }
    }
    private sealed class TrustFixture : IJavaTrustService
    {
        public int Calls;
        public Exception? Failure;
        public Func<JavaInstallation, JavaInstallation>? Transform;
        public JavaTrustCopyRequest? Request;
        public Task<IReadOnlyList<JavaTrustFinding>> InspectAsync(JavaInstallation java, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaTrustFinding>>([new(Path.Combine(java.JavaHome, "lib/security/cacerts"), true, "fixture-hash", true, true, "explicit fixture finding")]);
        public Task<JavaTrustDerivation> CreateRepairedCopyAsync(JavaTrustCopyRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Request = request;
            if (Failure is not null) return Task.FromException<JavaTrustDerivation>(Failure);
            var home = Path.Combine(request.DerivedRoot, "fixture-variant/home");
            var java = request.Source with { JavaHome = home, ExecutablePath = Path.Combine(home, "bin/java"), Source = "managed:SHOU-derived" };
            return Task.FromResult(new JavaTrustDerivation(Transform?.Invoke(java) ?? java, request.Source.ExecutablePath,
                "fixture-policy", "source-tree-fixture", "output-tree-fixture", Path.Combine(request.DerivedRoot, "fixture-variant/provenance.json"), []));
        }
    }
    private sealed class PipelineDownloads : IVerifiedDownloader
    {
        public static readonly byte[] Client = Encoding.UTF8.GetBytes("fixture-client");
        public static readonly byte[] Assets = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
        public int Calls;
        public Action? BeforeTransfer;
        public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            BeforeTransfer?.Invoke(); Calls++;
            var bytes = request.DestinationPath.EndsWith("fixture-assets.json", StringComparison.Ordinal) ? Assets : Client;
            Assert.AreEqual(request.Sha1, Hash(bytes));
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
        }
    }
}
