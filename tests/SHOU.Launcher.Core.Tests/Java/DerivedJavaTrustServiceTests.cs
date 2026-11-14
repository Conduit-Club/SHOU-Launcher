using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
[TestCategory("JavaTrust")]
public sealed class DerivedJavaTrustServiceTests
{
    [TestMethod]
    public async Task RepairsIndependentHomeAndReusesItWithoutChangingSourceOrCreatingMoreBackups()
    {
        var f = new Fixture(); var before = File.ReadAllBytes(f.Store);
        var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Store));
        Assert.IsFalse(File.Exists(f.Store + ".shou-trust-repair.json"));
        Assert.IsTrue(result.Findings.All(finding => finding.CanRepair && !finding.NeedsRepair));
        Assert.IsTrue(result.Findings.Any(finding => finding.CanRestore));
        Assert.AreNotEqual(result.SourceTreeSha256, result.OutputTreeSha256);
        Assert.AreEqual("managed:SHOUTrust", result.Java.Source);
        Assert.IsNull(result.Java.ValidatedManifestSha1, "A repaired variant is not byte-identical to Mojang's manifest.");
        Assert.AreEqual(f.Source.ExecutablePath, result.SourceJavaExecutable);
        var count = Directory.GetFiles(result.Java.JavaHome, "*.shou-backup-*", SearchOption.AllDirectories).Length;
        var second = await f.Service.CreateRepairedCopyAsync(f.Request);
        Assert.AreEqual(result.Java.ExecutablePath, second.Java.ExecutablePath);
        Assert.AreEqual(result.OutputTreeSha256, second.OutputTreeSha256);
        Assert.AreEqual(count, Directory.GetFiles(second.Java.JavaHome, "*.shou-backup-*", SearchOption.AllDirectories).Length);
        Assert.AreEqual(3, f.Discovery.Probed.Count, "Two initial location probes and one reuse probe.");
    }

    [TestMethod]
    public async Task AlreadyDerivedSourceIsRevalidatedWithoutRecursiveCopies()
    {
        var f = new Fixture(); var first = await f.Service.CreateRepairedCopyAsync(f.Request);
        var result = await f.Service.CreateRepairedCopyAsync(new(first.Java, f.DerivedRoot));
        Assert.AreEqual(first.Java.ExecutablePath, result.Java.ExecutablePath);
        Assert.AreEqual(f.Source.ExecutablePath, result.SourceJavaExecutable);
        Assert.AreEqual(1, Directory.GetDirectories(f.DerivedRoot).Count(path => !Path.GetFileName(path).StartsWith('.')));
    }

    [TestMethod]
    public async Task OriginalChangeRejectsReuseWithoutCreatingAnotherVariant()
    {
        var f = new Fixture(); await f.Service.CreateRepairedCopyAsync(f.Request);
        File.AppendAllText(Path.Combine(f.Source.JavaHome, "release"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        Assert.AreEqual(1, Directory.GetDirectories(f.DerivedRoot).Count(path => !Path.GetFileName(path).StartsWith('.')));
    }

    [TestMethod]
    public async Task ForgingOutputHashCannotEndorseAnAlteredJavaExecutable()
    {
        var f = new Fixture(); var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        File.AppendAllText(result.Java.ExecutablePath, "changed executable");
        var forged = JsonNode.Parse(File.ReadAllText(result.ProvenancePath))!;
        forged["OutputTreeSha256"] = await TreeHashAsync(result.Java.JavaHome);
        File.WriteAllText(result.ProvenancePath, forged.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(new(result.Java, f.DerivedRoot)));
    }

    [TestMethod]
    public async Task BackupTamperingAndModeChangesCannotBeReused()
    {
        var f = new Fixture(); var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        var backup = Directory.GetFiles(result.Java.JavaHome, "cacerts.shou-backup-*", SearchOption.AllDirectories).Single();
        File.AppendAllText(backup, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        var other = new Fixture(); var copy = await other.Service.CreateRepairedCopyAsync(other.Request);
        if (OperatingSystem.IsWindows()) File.SetAttributes(copy.Java.ExecutablePath, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(copy.Java.ExecutablePath, File.GetUnixFileMode(copy.Java.ExecutablePath) ^ UnixFileMode.GroupExecute);
        await Assert.ThrowsAsync<InvalidDataException>(() => other.Service.CreateRepairedCopyAsync(other.Request));
    }

    [TestMethod]
    public async Task RestoreReturnsExactOriginalAndMakesVariantUndiscoverableAndUnselectable()
    {
        var f = new Fixture(); var before = File.ReadAllBytes(f.Store);
        var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        var restored = JavaTrustRepair.Restore(result.Findings.Single(finding => finding.IsTrustStore));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(restored.Path));
        Assert.IsTrue(restored.NeedsRepair);
        Assert.IsFalse(DerivedJavaTrustService.IsCompletedVariantDirectory(Path.GetDirectoryName(result.Java.JavaHome)!));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(new(result.Java, f.DerivedRoot)));
        Assert.AreEqual(0, (await f.NoExecuteDiscovery().DiscoverAsync(new(null, f.DerivedRoot))).Count);
    }

    [TestMethod]
    public async Task FailedProbeAndMissingCompletionStateAreNeverDiscovered()
    {
        var f = new Fixture(); f.Discovery.Major = 17;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        Assert.AreEqual(0, (await f.NoExecuteDiscovery().DiscoverAsync(new(null, f.DerivedRoot))).Count);
        var stagedJava = Directory.GetFiles(Path.Combine(f.DerivedRoot, ".staging"), Fixture.JavaName, SearchOption.AllDirectories).Single();
        Assert.IsFalse((await f.NoExecuteDiscovery().ProbeAsync(stagedJava)).IsUsable, "Direct public probe must also reject an incomplete derived launcher.");
        var malformed = Path.Combine(f.DerivedRoot, new string('a', 64), "home", "bin");
        Directory.CreateDirectory(malformed); File.WriteAllText(Path.Combine(malformed, Fixture.JavaName), "not completed");
        Assert.AreEqual(0, (await f.NoExecuteDiscovery().DiscoverAsync(new(null, f.DerivedRoot))).Count);
    }

    [TestMethod]
    public async Task InspectAndPreCancelledRequestsNeverCopyOrProbe()
    {
        var f = new Fixture(); var findings = await f.Service.InspectAsync(f.Source);
        Assert.IsTrue(findings.Any(finding => finding.NeedsRepair));
        Assert.AreEqual(0, f.Discovery.Probed.Count); Assert.IsFalse(Directory.Exists(f.DerivedRoot));
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.CreateRepairedCopyAsync(f.Request, new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(f.DerivedRoot));
    }

    [TestMethod]
    public async Task UnvalidatedManifestClaimsAndOverlappingRootsFailBeforeCopy()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request with { SourceManifestSha1 = new string('b', 40) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(f.Request with { DerivedRoot = Path.Combine(f.Source.JavaHome, "derived-trust") }));
        var nested = f.Source with { JavaHome = Path.Combine(f.DerivedRoot, "unknown", "home") };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(new(nested, f.DerivedRoot)));
    }

    [TestMethod]
    public async Task HardLinkedSourceAndReplacedOutputAreRejected()
    {
        var f = new Fixture(); var alias = Path.Combine(f.Root, "source-hardlink");
        CreateHardLink(f.Source.ExecutablePath, alias);
        await Assert.ThrowsAsync<IOException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        var second = new Fixture(); var result = await second.Service.CreateRepairedCopyAsync(second.Request);
        File.Delete(result.Java.ExecutablePath);
        CreateHardLink(second.Source.ExecutablePath, result.Java.ExecutablePath);
        await Assert.ThrowsAsync<IOException>(() => second.Service.CreateRepairedCopyAsync(second.Request));
    }

    [TestMethod]
    [TestCategory("JavaTrustPosixLinks")]
    public async Task RelativeInternalLinksArePreservedAndOutsideBrokenOrAbsoluteLinksFailClosed()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("POSIX symbolic-link fixture; Windows junction tests require a native Windows gate.");
        var f = new Fixture(); File.CreateSymbolicLink(Path.Combine(f.Source.JavaHome, "release-alias"), "release");
        var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        Assert.AreEqual("release", new FileInfo(Path.Combine(result.Java.JavaHome, "release-alias")).LinkTarget);
        foreach (var target in new[] { "../outside", "missing", Path.Combine(f.Source.JavaHome, "release") })
        {
            var invalid = new Fixture();
            File.WriteAllText(Path.Combine(invalid.Root, "outside"), "outside");
            File.CreateSymbolicLink(Path.Combine(invalid.Source.JavaHome, "unsafe"), target);
            var original = File.ReadAllBytes(invalid.Store);
            if (target == "missing")
                await Assert.ThrowsAsync<IOException>(() => invalid.Service.CreateRepairedCopyAsync(invalid.Request));
            else await Assert.ThrowsAsync<InvalidDataException>(() => invalid.Service.CreateRepairedCopyAsync(invalid.Request));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(invalid.Store));
            Assert.AreEqual(0, invalid.Discovery.Probed.Count, "Unsafe sources must fail before even a copied runtime is probed.");
            Assert.AreEqual(0, Directory.GetDirectories(invalid.DerivedRoot).Count(path => !Path.GetFileName(path).StartsWith('.')));
        }
    }

    [TestMethod]
    [TestCategory("JavaTrustPosixLinks")]
    public async Task EscapingDerivedLauncherIsRejectedBeforeDiscoveryCanExecuteIt()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("POSIX symlink replacement gate; Windows execution remains a separate native gate.");
        var f = new Fixture(); var result = await f.Service.CreateRepairedCopyAsync(f.Request);
        File.Delete(result.Java.ExecutablePath);
        File.CreateSymbolicLink(result.Java.ExecutablePath, f.Source.ExecutablePath);
        var escapedProbe = f.Source with { ExecutablePath = result.Java.ExecutablePath };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.CreateRepairedCopyAsync(new(escapedProbe, f.DerivedRoot)));
        var discovered = await f.NoExecuteDiscovery().DiscoverAsync(new(result.Java.ExecutablePath, f.DerivedRoot));
        Assert.AreEqual(1, discovered.Count, "Configured failure remains visible without executing it.");
        Assert.IsFalse(discovered[0].IsUsable);
        StringAssert.Contains(discovered[0].ProbeError!, "InvalidDerivedRuntime");
        Assert.IsFalse((await f.NoExecuteDiscovery().ProbeAsync(result.Java.ExecutablePath)).IsUsable);
    }

    [TestMethod]
    [TestCategory("JavaTrustPosixLinks")]
    public async Task FailedKeytoolNeverReturnsRawOutputOrChangesSource()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("POSIX controlled failing-child fixture; real Windows keytool remains a native gate.");
        var f = new Fixture();
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(JavaTrustKernelTests.RootCertificate());
        var before = certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "changeit");
        File.WriteAllBytes(f.Store, before);
        var tool = Path.Combine(f.Source.JavaHome, "bin", "keytool");
        File.WriteAllText(tool, "#!/bin/sh\nprintf '%s' 'SYNTHETIC_TOOL_OUTPUT_MUST_NOT_ESCAPE' >&2\nexit 17\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var error = await Assert.ThrowsAsync<IOException>(() => f.Service.CreateRepairedCopyAsync(f.Request));
        Assert.IsFalse(error.ToString().Contains("SYNTHETIC_TOOL_OUTPUT_MUST_NOT_ESCAPE", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Store));
        Assert.AreEqual(0, (await f.NoExecuteDiscovery().DiscoverAsync(new(null, f.DerivedRoot))).Count);
    }

    internal static async Task<string> TreeHashAsync(string home)
    {
        // Use the production hash only to construct a forged hash in the adversarial-provenance test.
        // The assertion exercises independent transformation validation rather than duplicating its implementation.
        var type = typeof(DerivedJavaTrustService).Assembly.GetType("SHOU.Launcher.Core.Java.JavaTrustTree")!;
        var operation = (Task)type.GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [home, CancellationToken.None])!;
        await operation;
        var snapshot = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
        return (string)snapshot.GetType().GetProperty("Sha256")!.GetValue(snapshot)!;
    }

    private static void CreateHardLink(string source, string destination)
    {
        var success = OperatingSystem.IsWindows() ? NativeCreateHardLink(destination, source, IntPtr.Zero) : Link(source, destination) == 0;
        Assert.IsTrue(success, "The isolated test filesystem must support hard-link fixture creation.");
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCreateHardLink(string destination, string source, IntPtr security);

    private sealed class Fixture
    {
        internal static string JavaName => OperatingSystem.IsWindows() ? "java.exe" : "java";
        internal string Root { get; } = new PlatformFixture().Child("derived-trust-fixture");
        internal string DerivedRoot => Path.Combine(Root, "derived-trust");
        internal JavaInstallation Source { get; }
        internal string Store => Path.Combine(Source.JavaHome, "lib", "security", "cacerts");
        internal FixtureDiscovery Discovery { get; } = new();
        internal DerivedJavaTrustService Service { get; }
        internal JavaTrustCopyRequest Request => new(Source, DerivedRoot, Source.ValidatedManifestSha1);
        internal Fixture()
        {
            var home = Path.Combine(Root, "source-home"); var bin = Path.Combine(home, "bin");
            Directory.CreateDirectory(bin); Directory.CreateDirectory(Path.Combine(home, "lib", "security"));
            var java = Path.Combine(bin, JavaName); File.WriteAllText(java, "explicit Java probe fake");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=21.0.13\n");
            Source = new(java, home, "21.0.13", 21, "explicit fixture", RuntimeArchitecture.Arm64, "fixture", ValidatedManifestSha1: new string('a', 40));
            File.WriteAllBytes(Store, JavaTrustKernelTests.Jks(2, [("root", JavaTrustKernelTests.RootCertificate()), ("safe", JavaTrustKernelTests.SafeCertificate())]));
            Service = new(Discovery);
        }
        internal JavaDiscovery NoExecuteDiscovery() => new(new NeverExecuteRunner(),
            PlatformFixture.Context(OperatingSystem.IsWindows() ? LauncherOS.Windows : LauncherOS.MacOS), includeSystemSources: false,
            managedRuntimeDirectory: DerivedRoot);
    }
    private sealed class FixtureDiscovery : IJavaDiscovery
    {
        internal List<string> Probed { get; } = [];
        internal int Major { get; set; } = 21;
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Probed.Add(executable);
            return Task.FromResult(new JavaInstallation(executable, Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!,
                "21.0.13", Major, "explicit fixture", RuntimeArchitecture.Arm64, "fixture"));
        }
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
    private sealed class NeverExecuteRunner : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Incomplete variants must not execute.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Incomplete variants must not execute.");
    }
}
