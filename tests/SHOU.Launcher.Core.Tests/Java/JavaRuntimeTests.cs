using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class JavaRuntimeTests
{
    private const string Properties = """
        Property settings:
            java.home = /test/中文 runtime/Contents/Home
            java.version = 21.0.13
            java.runtime.version = 21.0.13+9-LTS
            os.arch = aarch64
        openjdk version "21.0.13" 2026-10-20 LTS
        """;

    [TestMethod]
    public void PropertiesAcrossStdoutAndStderrDescribeModernModularJre()
    {
        var installation = JavaRuntimeProbe.ParseOutput("/fixture/bin/java", "    java.vendor = Eclipse Adoptium\n", Properties);
        Assert.IsTrue(installation.IsUsable, installation.ProbeError);
        Assert.AreEqual(21, installation.MajorVersion);
        Assert.AreEqual("21.0.13+9-LTS", installation.FullVersion);
        Assert.AreEqual(RuntimeArchitecture.Arm64, installation.RuntimeArchitecture);
        Assert.AreEqual("/test/中文 runtime/Contents/Home", installation.JavaHome);
        Assert.AreEqual("Eclipse Adoptium", installation.Vendor);
    }

    [TestMethod]
    public void LegacyJavaVersionAndArchitectureAliasesRemainSupported()
    {
        var installation = JavaRuntimeProbe.ParseOutput("/fixture/java", "", Properties.Replace("21.0.13", "1.8.0_402").Replace("aarch64", "amd64"));
        Assert.AreEqual(8, installation.MajorVersion);
        Assert.AreEqual(RuntimeArchitecture.X64, installation.RuntimeArchitecture);
        Assert.AreEqual(RuntimeArchitecture.X86, JavaRuntimeProbe.NormalizeArchitecture("i686"));
        Assert.AreEqual(RuntimeArchitecture.Arm64, JavaRuntimeProbe.NormalizeArchitecture("arm64"));
    }

    [TestMethod]
    public void MissingPropertiesAndUnknownArchitectureAreNotUsable()
    {
        Assert.IsFalse(JavaRuntimeProbe.ParseOutput("/fixture/java", "", "openjdk version 21").IsUsable);
        var invalid = JavaRuntimeProbe.ParseOutput("/fixture/java", "", Properties.Replace("aarch64", "mystery64"));
        Assert.IsFalse(invalid.IsUsable);
        StringAssert.Contains(invalid.ProbeError, "UnknownArchitecture");
    }

    [TestMethod]
    public async Task ProbeExecutesRequiredArgumentsAndRejectsFailedOrTruncatedResults()
    {
        var runner = new ProbeFixtureRunner(new ProcessResult(0, "java.vendor = Test Vendor\n", Properties));
        var probe = new JavaRuntimeProbe(runner);
        var installation = await probe.ProbeAsync(PlatformFixture.JavaExecutable);
        Assert.IsTrue(installation.IsUsable);
        CollectionAssert.AreEqual(new[] { "-XX:-UsePerfData", "-XshowSettings:properties", "-version" }, runner.LastSpec!.Arguments.ToArray());
        Assert.IsTrue(runner.LastSpec.Timeout <= TimeSpan.FromSeconds(10));
        runner.Result = runner.Result with { ExitCode = 1 };
        StringAssert.Contains((await probe.ProbeAsync(PlatformFixture.JavaExecutable)).ProbeError, "FailedExit");
        runner.Result = runner.Result with { ExitCode = 0, OutputTruncated = true };
        StringAssert.Contains((await probe.ProbeAsync(PlatformFixture.JavaExecutable)).ProbeError, "OutputLimitExceeded");
        runner.Result = runner.Result with { OutputTruncated = false, TimedOut = true };
        StringAssert.Contains((await probe.ProbeAsync(PlatformFixture.JavaExecutable)).ProbeError, "TimedOut");
    }

    [TestMethod]
    public async Task MissingAndNonExecutableJavaHaveDistinctErrors()
    {
        var fixture = new PlatformFixture();
        await using var runner = new ProcessRunner();
        var probe = new JavaRuntimeProbe(runner);
        StringAssert.Contains((await probe.ProbeAsync(fixture.Child("missing-java"))).ProbeError, "NotFound");
        var invalid = fixture.Child(OperatingSystem.IsWindows() ? "invalid-java.exe" : "invalid-java");
        File.WriteAllText(invalid, "This is not an executable.");
        var result = await probe.ProbeAsync(invalid);
        Assert.IsFalse(result.IsUsable);
        StringAssert.Contains(result.ProbeError, OperatingSystem.IsWindows() ? "BadExecutableOrArchitecture" : "NotExecutable");
    }

    [TestMethod]
    public async Task ControlledJava21ProbeReportsRealHomeVersionAndNativeArchitecture()
    {
        await using var runner = new ProcessRunner();
        var installation = await new JavaRuntimeProbe(runner).ProbeAsync(PlatformFixture.JavaExecutable);
        Assert.IsTrue(installation.IsUsable, installation.ProbeError);
        Assert.AreEqual(21, installation.MajorVersion, "The registered native test JDK is Temurin 21.");
        Assert.AreEqual(new NativePlatformInfo().Context.HardwareArchitecture, installation.RuntimeArchitecture);
        Assert.IsTrue(Directory.Exists(installation.JavaHome));
        Assert.AreEqual(PlatformFixture.JavaExecutable, installation.ExecutablePath);
    }

    [TestMethod]
    public async Task ManagedBundleDiscoveryKeepsInvalidConfiguredPathVisibleAndSkipsLinkLoops()
    {
        var fixture = new PlatformFixture();
        var managed = fixture.Child("runtimes");
        var bin = Path.Combine(managed, "java-runtime-delta", "jre.bundle", "Contents", "Home", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "java"), "explicit test JVM fixture");
        if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(managed, "loop"), managed);
        var discovery = new JavaDiscovery(new ProbeFixtureRunner(new ProcessResult(0, "", Properties)),
            PlatformFixture.Context(LauncherOS.MacOS), includeSystemSources: false);
        var result = await discovery.DiscoverAsync(new JavaDiscoveryRequest(fixture.Child("missing-java"), managed, 21, RuntimeArchitecture.Arm64));
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("managed", result[0].Source);
        Assert.IsTrue(result[0].IsUsable);
        Assert.AreEqual("configured", result[1].Source);
        Assert.IsFalse(result[1].IsUsable);
    }

    [TestMethod]
    public void SelectionUsesMetadataMajorAndActualJavaArchitecture()
    {
        var java = JavaRuntimeProbe.ParseOutput("/java21-arm64", "", Properties);
        JavaInstallation[] installations =
        [
            java with { ExecutablePath = "/java25-arm64", MajorVersion = 25 },
            java with { ExecutablePath = "/java21-x64", RuntimeArchitecture = RuntimeArchitecture.X64 },
            java with { ExecutablePath = "/java17-arm64", MajorVersion = 17 },
            java
        ];
        Assert.AreEqual(java, JavaSelection.Select(installations, 21, RuntimeArchitecture.Arm64));
        Assert.AreEqual(17, JavaSelection.Select(installations, 17, RuntimeArchitecture.Arm64)!.MajorVersion);
        Assert.AreEqual(25, JavaSelection.Select(installations, 25, RuntimeArchitecture.Arm64)!.MajorVersion);
        Assert.IsNull(JavaSelection.Select(installations, 8, RuntimeArchitecture.Arm64));
        Assert.IsNull(JavaSelection.Select(installations, 21, RuntimeArchitecture.Arm64, "/java21-x64"), "A preferred incompatible JVM must not silently fall back.");
    }

    private sealed class ProbeFixtureRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessResult Result { get; set; } = result;
        public ProcessSpec? LastSpec { get; private set; }
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("This explicit unit-test fake only supplies probe responses.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSpec = spec;
            return Task.FromResult(Result);
        }
    }
}
