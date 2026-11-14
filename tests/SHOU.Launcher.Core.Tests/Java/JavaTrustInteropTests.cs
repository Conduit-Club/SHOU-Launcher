using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
[TestCategory("JavaTrustInterop")]
public sealed class JavaTrustInteropTests
{
    [TestMethod]
    public async Task RealB5RuntimeCopyRepairsAndProbesWithoutTouchingOriginalThenReusesSameVariant()
    {
        var (home, work) = Input();
        await using var runner = new ProcessRunner();
        var isolated = new IsolatedProbeRunner(runner, work);
        var discovery = new JavaDiscovery(isolated, Context(), includeSystemSources: false, managedRuntimeDirectory: Path.Combine(work, "derived-trust"));
        var source = await discovery.ProbeAsync(Path.Combine(home, "bin", JavaName));
        Assert.IsTrue(source.IsUsable, source.ProbeError);
        var sourceHash = await DerivedJavaTrustServiceTests.TreeHashAsync(home);
        var sourceStore = File.ReadAllBytes(Path.Combine(home, "lib", "security", "cacerts"));
        var sourceBlocked = File.ReadAllBytes(Path.Combine(home, "lib", "security", "blocked.certs"));
        var service = new DerivedJavaTrustService(discovery, isolated);
        var request = new JavaTrustCopyRequest(source, Path.Combine(work, "derived-trust"));
        var result = await service.CreateRepairedCopyAsync(request);
        Assert.AreEqual(sourceHash, await DerivedJavaTrustServiceTests.TreeHashAsync(home));
        CollectionAssert.AreEqual(sourceStore, File.ReadAllBytes(Path.Combine(home, "lib", "security", "cacerts")));
        CollectionAssert.AreEqual(sourceBlocked, File.ReadAllBytes(Path.Combine(home, "lib", "security", "blocked.certs")));
        Assert.AreEqual(source.RuntimeArchitecture, result.Java.RuntimeArchitecture);
        Assert.AreEqual(source.MajorVersion, result.Java.MajorVersion);
        Assert.IsTrue(result.Findings.All(finding => finding.CanRepair && !finding.NeedsRepair));
        Assert.AreNotEqual(sourceHash, result.OutputTreeSha256);
        var second = await service.CreateRepairedCopyAsync(request);
        var already = await service.CreateRepairedCopyAsync(new(result.Java, request.DerivedRoot));
        Assert.AreEqual(result.Java.ExecutablePath, second.Java.ExecutablePath);
        Assert.AreEqual(result.Java.ExecutablePath, already.Java.ExecutablePath);
        Assert.AreEqual(result.OutputTreeSha256, already.OutputTreeSha256);
        var discovered = await discovery.DiscoverAsync(new(null, request.DerivedRoot));
        Assert.AreEqual(1, discovered.Count, "Completed owned variant passes the no-process validator before discovery probes it.");
        Assert.IsTrue(discovered[0].IsUsable);
        Assert.AreEqual(sourceHash, await DerivedJavaTrustServiceTests.TreeHashAsync(home));
        await WriteEvidenceAsync(work, new
        {
            status = "PASS", evidence = "Real copied B5 Microsoft Java21 runtime, relocated probes and exact policy kernel; original readonly",
            source, result, reuse = second.Java.ExecutablePath, alreadyDerivedReuse = already.Java.ExecutablePath,
            sourceUnchangedSha256 = sourceHash, probeCount = isolated.Calls,
            originalCacertsSha256 = Convert.ToHexString(SHA256.HashData(sourceStore)),
            originalBlocklistSha256 = Convert.ToHexString(SHA256.HashData(sourceBlocked))
        });
    }

    [TestMethod]
    public async Task CopiedRuntimeKeytoolRepairsGenuinePkcs12PreservesOtherCertificateAndRestoresExactBytes()
    {
        var (originalHome, work) = Input();
        var home = Path.Combine(work, "pkcs12-source-home");
        CopyFixtureHome(originalHome, home);
        var store = Path.Combine(home, "lib", "security", "cacerts");
        File.Move(store, store + ".original-fixture");
        var rootPem = Path.Combine(work, "root.pem"); var safePem = Path.Combine(work, "safe.pem");
        File.WriteAllText(rootPem, JavaTrustKernelTests.RootPem);
        var safeBytes = JavaTrustKernelTests.SafeCertificate();
        File.WriteAllText(safePem, PemEncoding.WriteString("CERTIFICATE", safeBytes));
        await using var runner = new ProcessRunner();
        var tool = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");
        foreach (var (alias, certificate) in new[] { ("arbitrary-root", rootPem), ("safe-certificate", safePem) })
        {
            var environment = CleanEnvironment(); environment["SHOU_TRUST_PASSWORD"] = "changeit";
            var generated = await runner.RunAsync(new ProcessSpec(tool,
                ["-J-XX:-UsePerfData", "-J-Djava.io.tmpdir=" + work, "-J-Duser.language=en", "-J-Duser.country=US",
                    "-keystore", store, "-storepass:env", "SHOU_TRUST_PASSWORD", "-storetype", "PKCS12",
                    "-importcert", "-noprompt", "-alias", alias, "-file", certificate],
                work, environment, TimeSpan.FromSeconds(30), 131072));
            Assert.AreEqual(0, generated.ExitCode, "Isolated fixture keytool must succeed; raw tool output is intentionally omitted.");
            Assert.IsFalse(generated.TimedOut || generated.Cancelled || generated.OutputTruncated);
        }
        var before = File.ReadAllBytes(store);
        Assert.AreNotEqual("FEEDFEED", Convert.ToHexString(before.AsSpan(0, 4)));
        var isolated = new IsolatedProbeRunner(runner, work);
        var discovery = new JavaDiscovery(isolated, Context(), includeSystemSources: false, managedRuntimeDirectory: Path.Combine(work, "derived-trust"));
        var source = await discovery.ProbeAsync(Path.Combine(home, "bin", JavaName));
        Assert.IsTrue(source.IsUsable, source.ProbeError);
        var service = new DerivedJavaTrustService(discovery, isolated);
        var result = await service.CreateRepairedCopyAsync(new(source, Path.Combine(work, "derived-trust")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store));
        var copiedStore = result.Findings.Single(finding => finding.IsTrustStore);
        var after = File.ReadAllBytes(copiedStore.Path);
        var fingerprints = Pkcs12TrustReader.ReadFingerprints(after);
        Assert.AreEqual(1, fingerprints.Count);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(safeBytes)), fingerprints[0]);
        var reused = await service.CreateRepairedCopyAsync(new(source, Path.Combine(work, "derived-trust")));
        Assert.AreEqual(result.Java.ExecutablePath, reused.Java.ExecutablePath);
        JavaTrustRepair.Restore(copiedStore);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(copiedStore.Path));
        Assert.IsFalse(DerivedJavaTrustService.IsCompletedVariantDirectory(Path.GetDirectoryName(result.Java.JavaHome)!));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateRepairedCopyAsync(new(result.Java, Path.Combine(work, "derived-trust"))));
        await WriteEvidenceAsync(work, new
        {
            status = "PASS", evidence = "Production repair kernel used copied same-JDK keytool on genuine trusted-certificate PKCS12 fixture",
            source, result, sourceStoreUnchangedSha256 = Convert.ToHexString(SHA256.HashData(before)),
            repairedStoreSha256 = Convert.ToHexString(SHA256.HashData(after)), safeCertificateSha256 = Convert.ToHexString(SHA256.HashData(safeBytes)),
            restoredExactOriginalBytes = true, restoredVariantSelectable = false
        });
    }

    private static (string Home, string Work) Input()
    {
        if (Environment.GetEnvironmentVariable("SHOU_RUN_JAVA_TRUST_INTEROP") != "1")
            Assert.Inconclusive("Explicit controlled runtime-copy gate is disabled; no JDK or filesystem scan performed.");
        var root = Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Explicit task root required.");
        var home = Environment.GetEnvironmentVariable("SHOU_JAVA_TRUST_SOURCE_HOME") ?? throw new InvalidOperationException("Explicit B5 runtime source required.");
        var allowed = Path.GetFullPath(Path.Combine(root, "test-data", "platform", "OnlineJava")) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(home) || !Path.GetFullPath(home).StartsWith(allowed, StringComparison.Ordinal))
            throw new InvalidOperationException("Interop source must be the task-owned B5 runtime; tools/user JDKs are forbidden.");
        JavaTrustRepair.EnsureRegularPath(home);
        var work = new PlatformFixture().Child("B8-real-runtime"); Directory.CreateDirectory(work);
        return (home, work);
    }
    private static string JavaName => OperatingSystem.IsWindows() ? "java.exe" : "java";
    private static PlatformContext Context() => PlatformFixture.Context(OperatingSystem.IsWindows() ? LauncherOS.Windows
        : OperatingSystem.IsMacOS() ? LauncherOS.MacOS : LauncherOS.Linux);
    private static async Task WriteEvidenceAsync<T>(string work, T evidence)
    {
        var path = Path.Combine(work, "trust-interop-evidence.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("JavaTrustEvidence=" + path);
    }
    private static Dictionary<string, string?> CleanEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Environment.GetEnvironmentVariables().Keys)
            if (key.StartsWith("PCL_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CURSEFORGE_", StringComparison.OrdinalIgnoreCase)
                || new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH", "JAVA_HOME" }.Contains(key, StringComparer.OrdinalIgnoreCase)) result[key] = null;
        return result;
    }
    private sealed class IsolatedProbeRunner(ProcessRunner inner, string work) : IProcessRunner
    {
        internal int Calls { get; private set; }
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) =>
            inner.StartAsync(Isolate(spec), output, cancellationToken);
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
        {
            Calls++; return inner.RunAsync(Isolate(spec), output, cancellationToken);
        }
        private ProcessSpec Isolate(ProcessSpec spec)
        {
            var env = CleanEnvironment(); env["TMPDIR"] = work; env["TMP"] = work; env["TEMP"] = work;
            return spec with { Arguments = new[] { "-Djava.io.tmpdir=" + work }.Concat(spec.Arguments).ToArray(), EnvironmentOverrides = env };
        }
    }
    private static void CopyFixtureHome(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var dirs = new List<(string Source, string Destination)> { (source, destination) };
        var queue = new Queue<(string Source, string Destination)>(); queue.Enqueue((source, destination));
        while (queue.TryDequeue(out var pair))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(pair.Source))
            {
                var target = Path.Combine(pair.Destination, Path.GetFileName(path));
                var attr = File.GetAttributes(path); FileSystemInfo info = (attr & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                if (info.LinkTarget is { } link)
                {
                    if (Path.IsPathRooted(link)) throw new InvalidDataException("Fixture requires relative runtime links.");
                    if (Directory.Exists(path)) Directory.CreateSymbolicLink(target, link); else File.CreateSymbolicLink(target, link);
                }
                else if ((attr & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target); dirs.Add((path, target)); queue.Enqueue((path, target));
                }
                else
                {
                    File.Copy(path, target, false);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(path));
                }
            }
        }
        if (!OperatingSystem.IsWindows())
            foreach (var pair in dirs.OrderByDescending(pair => pair.Destination.Length)) File.SetUnixFileMode(pair.Destination, File.GetUnixFileMode(pair.Source));
    }
}
