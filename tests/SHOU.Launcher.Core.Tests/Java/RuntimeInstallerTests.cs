using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class RuntimeInstallerTests
{
    [TestMethod]
    public async Task ManifestInstallsFilesDirectoriesModesAndLinksThenProbesBothLocations()
    {
        var fixture = new RuntimeFixture();
        var result = await fixture.Installer.InstallAsync(fixture.Request);
        Assert.IsTrue(result.IsUsable);
        Assert.AreEqual("managed:Mojang", result.Source);
        Assert.AreEqual(Path.GetFileName(fixture.FinalDirectory), result.ValidatedManifestSha1);
        Assert.AreEqual(2, fixture.Discovery.Probed.Count);
        StringAssert.Contains(fixture.Discovery.Probed[0], ".staging");
        Assert.IsFalse(result.ExecutablePath.Contains(".staging", StringComparison.Ordinal));
        var home = Path.GetDirectoryName(Path.GetDirectoryName(result.ExecutablePath)!)!;
        CollectionAssert.AreEqual(RuntimeFixture.ReleaseBytes, File.ReadAllBytes(Path.Combine(home, "release")));
        Assert.IsFalse(MojangJavaRuntimeInstaller.IsIncompleteRuntime(fixture.FinalDirectory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue((File.GetUnixFileMode(result.ExecutablePath) & UnixFileMode.UserExecute) != 0);
            Assert.IsTrue((File.GetUnixFileMode(Path.Combine(home, "release")) & UnixFileMode.UserExecute) == 0);
            Assert.AreEqual("release", new FileInfo(Path.Combine(home, "release-link")).LinkTarget);
        }
        var second = await fixture.Installer.InstallAsync(fixture.Request);
        Assert.AreEqual(result.ExecutablePath, second.ExecutablePath);
        Assert.AreEqual(result.ValidatedManifestSha1, second.ValidatedManifestSha1);
        Assert.AreEqual(2, fixture.Downloader.CatalogFetches, "The root catalog must be fetched fresh for every installation request.");
    }

    [TestMethod]
    public async Task CatalogNeverFallsBackToDifferentMajorOrArchitecture()
    {
        var fixture = new RuntimeFixture();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => fixture.Installer.InstallAsync(fixture.Request with { MajorVersion = 25 }));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => fixture.Installer.InstallAsync(fixture.Request with { Architecture = RuntimeArchitecture.X64 }));
        Assert.AreEqual(0, fixture.Discovery.Probed.Count);
    }

    [TestMethod]
    public async Task ManifestHashFailureDoesNotCreateAUsableRuntime()
    {
        var fixture = new RuntimeFixture();
        fixture.Downloader.Resources[fixture.ManifestUri] = Encoding.UTF8.GetBytes("corrupt manifest");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        Assert.IsFalse(Directory.Exists(fixture.FinalDirectory));
        Assert.AreEqual(0, fixture.Discovery.Probed.Count);
    }

    [TestMethod]
    public async Task FileHashFailureLeavesStagingMarkedIncompleteAndDiscoveryIgnoresIt()
    {
        var fixture = new RuntimeFixture();
        fixture.Downloader.Resources[RuntimeFixture.JavaUri] = Encoding.UTF8.GetBytes("wrong Java bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        Assert.IsFalse(Directory.Exists(fixture.FinalDirectory));
        var stage = Directory.GetDirectories(Path.Combine(fixture.Request.InstallRoot, ".staging")).Single();
        Assert.IsTrue(MojangJavaRuntimeInstaller.IsIncompleteRuntime(stage));
        // Even a locally executable-looking file in a failed staging tree must not be advertised by discovery.
        var bin = Path.Combine(stage, "bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "java"), "fixture");
        var scan = new JavaDiscovery(new ProbeRunner(), PlatformFixture.Context(LauncherOS.MacOS), includeSystemSources: false);
        Assert.AreEqual(0, (await scan.DiscoverAsync(new JavaDiscoveryRequest(null, fixture.Request.InstallRoot))).Count);
    }

    [TestMethod]
    public async Task TraversalAndEscapingLinkTargetsAreRejectedBeforeFileDownloads()
    {
        var fixture = new RuntimeFixture("../outside");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        Assert.AreEqual(0, fixture.Downloader.RuntimeFetches);
        var link = new RuntimeFixture(linkTarget: "../../../../../../outside");
        await Assert.ThrowsAsync<InvalidDataException>(() => link.Installer.InstallAsync(link.Request));
        Assert.AreEqual(0, link.Downloader.RuntimeFetches);
    }

    [TestMethod]
    public async Task ProbeArchitectureMismatchCannotWriteCompletedMarker()
    {
        var fixture = new RuntimeFixture();
        fixture.Discovery.Architecture = RuntimeArchitecture.X64;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        Assert.IsFalse(Directory.Exists(fixture.FinalDirectory));
        var stage = Directory.GetDirectories(Path.Combine(fixture.Request.InstallRoot, ".staging")).Single();
        Assert.IsTrue(MojangJavaRuntimeInstaller.IsIncompleteRuntime(stage));
    }

    [TestMethod]
    public async Task ExistingRuntimeCorruptionIsPreservedAndNotRegisteredAsSuccess()
    {
        var fixture = new RuntimeFixture();
        var first = await fixture.Installer.InstallAsync(fixture.Request);
        var damaged = Encoding.UTF8.GetBytes("user changed bytes");
        File.WriteAllBytes(first.ExecutablePath, damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(first.ExecutablePath));
    }

    [TestMethod]
    public async Task RequestedInstallRootCannotEscapeManagedRoot()
    {
        var fixture = new RuntimeFixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request with
        { InstallRoot = Path.Combine(fixture.Root, "outside") }));
        Assert.AreEqual(0, fixture.Downloader.CatalogFetches);
    }

    [TestMethod]
    public async Task ManifestCannotOverwriteInstallerCompletionOrLockState()
    {
        foreach (var name in new[] { MojangJavaRuntimeInstaller.StateFileName, ".SHOU-RUNTIME.JSON/payload", ".locks/x", ".staging/x" })
        {
            var fixture = new RuntimeFixture(name);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
            Assert.AreEqual(0, fixture.Downloader.RuntimeFetches);
        }
    }

    [TestMethod]
    public void MalformedOversizedOrLinkedRuntimeStateCannotAdvertiseCompletion()
    {
        var fixture = new PlatformFixture();
        var large = fixture.Child("large state"); Directory.CreateDirectory(large);
        File.WriteAllText(Path.Combine(large, MojangJavaRuntimeInstaller.StateFileName), new string('x', 65537));
        Assert.IsTrue(MojangJavaRuntimeInstaller.IsIncompleteRuntime(large));
        var broken = fixture.Child("broken state"); Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, MojangJavaRuntimeInstaller.StateFileName), "not json");
        Assert.IsTrue(MojangJavaRuntimeInstaller.IsIncompleteRuntime(broken));
        if (!OperatingSystem.IsWindows())
        {
            var linked = fixture.Child("linked state"); Directory.CreateDirectory(linked);
            File.CreateSymbolicLink(Path.Combine(linked, MojangJavaRuntimeInstaller.StateFileName), fixture.Child("missing-state"));
            Assert.IsTrue(MojangJavaRuntimeInstaller.IsIncompleteRuntime(linked));
        }
    }

    private sealed class RuntimeFixture
    {
        public static readonly byte[] ReleaseBytes = "JAVA_VERSION=21.0.13\n"u8.ToArray();
        public static readonly Uri JavaUri = new("https://runtime-fixture.invalid/java");
        public Uri ManifestUri { get; } = new("https://runtime-fixture.invalid/manifest.json");
        public string Root { get; } = new PlatformFixture().Root;
        public FixtureDownloader Downloader { get; } = new();
        public FixtureDiscovery Discovery { get; } = new();
        public JavaRuntimeRequest Request { get; }
        public MojangJavaRuntimeInstaller Installer { get; }
        public string FinalDirectory { get; }

        public RuntimeFixture(string? additionalPath = null, string? linkTarget = null)
        {
            var files = new JsonObject
            {
                ["jre.bundle"] = new JsonObject { ["type"] = "directory" },
                ["jre.bundle/Contents/Home/bin/java"] = FileEntry(JavaUri, "explicit executable fixture"u8.ToArray(), true),
                ["jre.bundle/Contents/Home/release"] = FileEntry(new Uri("https://runtime-fixture.invalid/release"), ReleaseBytes, false)
            };
            if (!OperatingSystem.IsWindows() || linkTarget is not null)
                files["jre.bundle/Contents/Home/release-link"] = new JsonObject { ["type"] = "link", ["target"] = linkTarget ?? "release" };
            if (additionalPath is not null) files[additionalPath] = new JsonObject { ["type"] = "directory" };
            var manifest = Encoding.UTF8.GetBytes(new JsonObject { ["files"] = files }.ToJsonString());
            var sha = Convert.ToHexString(SHA1.HashData(manifest)).ToLowerInvariant();
            Downloader.Resources[ManifestUri] = manifest;
            var catalogUri = new Uri("https://runtime-fixture.invalid/all.json");
            var catalog = new JsonObject
            {
                ["mac-os-arm64"] = new JsonObject
                {
                    ["java-runtime-delta"] = new JsonArray(new JsonObject
                    {
                        ["version"] = new JsonObject { ["name"] = "21.0.13" },
                        ["manifest"] = new JsonObject { ["url"] = ManifestUri.AbsoluteUri, ["sha1"] = sha, ["size"] = manifest.Length }
                    })
                }
            };
            Downloader.Resources[catalogUri] = Encoding.UTF8.GetBytes(catalog.ToJsonString());
            var runtimes = Path.Combine(Root, "runtimes");
            Request = new JavaRuntimeRequest("java-runtime-delta", 21, LauncherOS.MacOS, RuntimeArchitecture.Arm64, runtimes, catalogUri);
            FinalDirectory = Path.Combine(runtimes, "java-runtime-delta", "mac-os-arm64", sha);
            Installer = new MojangJavaRuntimeInstaller(Downloader, Discovery, runtimes, Path.Combine(Root, "metadata"));
        }

        private JsonObject FileEntry(Uri uri, byte[] bytes, bool executable)
        {
            Downloader.Resources[uri] = bytes;
            return new JsonObject { ["type"] = "file", ["executable"] = executable, ["downloads"] = new JsonObject
            { ["raw"] = new JsonObject { ["url"] = uri.AbsoluteUri, ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), ["size"] = bytes.Length } } };
        }
    }

    private sealed class FixtureDownloader : IVerifiedDownloader
    {
        public Dictionary<Uri, byte[]> Resources { get; } = new();
        private int _catalogFetches;
        private int _runtimeFetches;
        public int CatalogFetches => _catalogFetches;
        public int RuntimeFetches => _runtimeFetches;
        public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var uri = request.Sources.Single();
            if (uri.AbsolutePath.EndsWith("all.json")) Interlocked.Increment(ref _catalogFetches);
            else if (!uri.AbsolutePath.EndsWith("manifest.json")) Interlocked.Increment(ref _runtimeFetches);
            var bytes = Resources[uri];
            if (request.Size.HasValue && request.Size.Value != bytes.Length) throw new InvalidDataException("Fixture size mismatch");
            if (request.Sha1 is { } hash && !Convert.ToHexString(SHA1.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture hash mismatch");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
        }
    }

    private sealed class FixtureDiscovery : IJavaDiscovery
    {
        public List<string> Probed { get; } = [];
        public RuntimeArchitecture Architecture { get; set; } = RuntimeArchitecture.Arm64;
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default)
        {
            Probed.Add(executable);
            return Task.FromResult(new JavaInstallation(executable, Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!,
                "21.0.13", 21, "explicit test fake", Architecture, "fixture"));
        }
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Installer fixture only probes its explicit executable.");
    }

    private sealed class ProbeRunner : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Incomplete staging must not be executed by discovery.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Incomplete staging must not be executed by discovery.");
    }
}
