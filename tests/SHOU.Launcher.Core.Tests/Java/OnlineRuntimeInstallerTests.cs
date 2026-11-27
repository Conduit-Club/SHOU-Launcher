using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class OnlineRuntimeInstallerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("OnlineJava")]
    [Timeout(900000)]
    public async Task OfficialDelta21InstallsProbesAndReusesOnlyItsManagedMacArm64Runtime()
    {
        // Both the explicit opt-in and the OnlineJava filter belong in the controlled dev.py invocation.
        // Without opt-in, report Inconclusive before creating roots, clients or child processes.
        if (Environment.GetEnvironmentVariable("SHOU_RUN_ONLINE_JAVA") != "1")
        {
            Assert.Inconclusive("OnlineJava is disabled. Use SHOU_RUN_ONLINE_JAVA=1 and --filter TestCategory=OnlineJava through control/dev.py.");
            return;
        }
        var platform = new NativePlatformInfo().Context;
        Assert.AreEqual(LauncherOS.MacOS, platform.OS, "This online gate targets a native macOS ARM64 host.");
        Assert.AreEqual(RuntimeArchitecture.Arm64, platform.ProcessArchitecture);
        Assert.AreEqual(RuntimeArchitecture.Arm64, platform.HardwareArchitecture);
        var taskRoot = Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")
            ?? throw new InvalidOperationException("OnlineJava requires the registered control/dev.py task environment.");
        if (!Path.IsPathFullyQualified(taskRoot)) throw new InvalidOperationException("SHOU_DEV_ROOT must be absolute.");
        var root = Path.Combine(taskRoot, "test-data", "platform", "OnlineJava", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "online-java-report.json");
        var report = new OnlineRuntimeReport(root);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        using var downloader = new OnlineRuntimeHttpDownloader(root);
        await using var realProcesses = new ProcessRunner();
        var runner = new IsolatedJvmProcesses(realProcesses, root);
        // System discovery is intentionally disabled: do not inspect PATH, JAVA_HOME, registry or user JDKs.
        var discovery = new JavaDiscovery(runner, platform, homeDirectory: root, includeSystemSources: false);
        var runtimes = Path.Combine(root, "runtimes");
        var metadata = Path.Combine(root, "metadata");
        var request = new JavaRuntimeRequest("java-runtime-delta", 21, LauncherOS.MacOS,
            RuntimeArchitecture.Arm64, runtimes);
        try
        {
            report.Stage = "InstallingOfficialRuntime";
            var installer = new MojangJavaRuntimeInstaller(downloader, discovery, runtimes, metadata);
            var first = await installer.InstallAsync(request, cancellationToken: ct);
            AssertJava(first, runtimes);
            report.FirstProbe = first;
            var firstReceipts = downloader.Receipts;
            var catalogReceipt = firstReceipts.Single(r => r.Url == MojangJavaRuntimeInstaller.OfficialCatalog.AbsoluteUri);
            Assert.IsFalse(catalogReceipt.CacheHit);
            var catalog = await ReadObjectAsync(Path.Combine(root, catalogReceipt.DestinationRelativePath), ct);
            var entry = catalog["mac-os-arm64"]?[request.Component]?.AsArray().OfType<JsonObject>()
                .FirstOrDefault(candidate => ((string?)candidate["version"]?["name"])?.Split('.', '-', '+', '_')[0] == "21")
                ?? throw new InvalidDataException("Official catalog lacks the requested mac-os-arm64/Java 21 entry.");
            var manifestDescriptor = entry["manifest"]!.AsObject();
            var manifestSha1 = manifestDescriptor["sha1"]!.GetValue<string>();
            var manifestReceipt = firstReceipts.Single(r => r.Url == manifestDescriptor["url"]!.GetValue<string>());
            Assert.AreEqual(manifestSha1, manifestReceipt.Sha1);
            Assert.AreEqual(manifestDescriptor["size"]!.GetValue<long>(), manifestReceipt.Size);
            report.CatalogVersion = (string?)entry["version"]?["name"];
            report.ManifestUrl = manifestReceipt.Url; report.ManifestSha1 = manifestReceipt.Sha1;
            report.ManifestSha256 = manifestReceipt.Sha256;
            var files = (await ReadObjectAsync(Path.Combine(root, manifestReceipt.DestinationRelativePath), ct))["files"]!.AsObject();
            var final = Path.Combine(runtimes, request.Component, "mac-os-arm64", manifestSha1);
            Assert.IsTrue(first.ExecutablePath.StartsWith(final + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.IsTrue(first.ExecutablePath.Contains(".bundle/Contents/Home/bin/java", StringComparison.Ordinal));
            report.Stage = "VerifyingManifestFilesModesAndLinks";
            report.ManifestEntries = await VerifyManifestAsync(final, files, ct);
            report.DeclaredRawBytes = report.ManifestEntries.Sum(f => f.Size ?? 0);
            report.RegularFileCount = report.ManifestEntries.Count(f => f.Type == "file");
            report.LinkCount = report.ManifestEntries.Count(f => f.Type == "link");
            Assert.AreEqual(report.DeclaredRawBytes, firstReceipts.Where(r => !r.Metadata).Sum(r => r.NetworkBytes));
            AssertMarker(final, manifestSha1, first.ExecutablePath);
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(runtimes, ".staging")).Length);

            report.Stage = "IndependentFinalJvmProbe";
            var independentProbe = await new JavaRuntimeProbe(runner).ProbeAsync(first.ExecutablePath, ct);
            report.IndependentFinalProbe = independentProbe;
            AssertJava(independentProbe, runtimes);
            Assert.AreEqual(first.FullVersion, independentProbe.FullVersion);
            var stamps = report.ManifestEntries.Where(f => f.Type == "file").ToDictionary(f => f.Path,
                f => File.GetLastWriteTimeUtc(Path.Combine(final, f.Path)));

            report.Stage = "ReusingVerifiedInstallation";
            downloader.ForbidRuntimePayloadDownloads = true;
            var second = await installer.InstallAsync(request, cancellationToken: ct);
            AssertJava(second, runtimes);
            Assert.AreEqual(first.ExecutablePath, second.ExecutablePath);
            var reuse = downloader.Receipts.Skip(firstReceipts.Count).ToArray();
            Assert.AreEqual(2, reuse.Length, "Reuse should request only fresh root catalog and verified cached manifest.");
            Assert.AreEqual(1, reuse.Count(r => r.Url == MojangJavaRuntimeInstaller.OfficialCatalog.AbsoluteUri && !r.CacheHit));
            Assert.AreEqual(1, reuse.Count(r => r.Url == report.ManifestUrl && r.CacheHit));
            Assert.IsTrue(reuse.All(r => r.Metadata));
            foreach (var (path, modified) in stamps)
                Assert.AreEqual(modified, File.GetLastWriteTimeUtc(Path.Combine(final, path)), "Reuse rewrote a runtime payload: " + path);
            AssertMarker(final, manifestSha1, second.ExecutablePath);
            report.SecondProbe = second;
            report.ReusePayloadNetworkBytes = reuse.Where(r => !r.Metadata).Sum(r => r.NetworkBytes);
            report.ReuseCatalogFresh = true; report.ReuseManifestCacheVerified = true;

            report.Stage = "ManagedOnlyDiscovery";
            var discovered = await discovery.DiscoverAsync(new(null, runtimes, 21, RuntimeArchitecture.Arm64), ct);
            Assert.IsTrue(discovered.Any(j => j.ExecutablePath == first.ExecutablePath && j.IsUsable));
            foreach (var java in discovered) AssertJava(java, runtimes);
            report.Discovered = discovered;
            report.Status = "PASS"; report.Stage = "Completed";
        }
        catch (Exception error)
        {
            report.Status = "FAIL"; report.ErrorType = error.GetType().FullName;
            // Preserve downloads and partial installation; never install a substitute or touch a system JDK.
            throw;
        }
        finally
        {
            report.Receipts = downloader.Receipts;
            report.FailedRequests = downloader.Failures;
            report.TotalNetworkBytes = downloader.TotalNetworkBytes;
            report.FinishedAt = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.WriteLine("OnlineJava report: " + reportPath);
            TestContext.AddResultFile(reportPath);
        }
    }

    // Keep JVM scratch/perf data isolated without changing HOME, global Java or production probe logic.
    private sealed class IsolatedJvmProcesses(ProcessRunner inner, string root) : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
            CancellationToken cancellationToken = default) => inner.StartAsync(Isolate(spec), output, cancellationToken);
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null,
            CancellationToken cancellationToken = default) => inner.RunAsync(Isolate(spec), output, cancellationToken);
        private ProcessSpec Isolate(ProcessSpec spec)
        {
            if (!spec.FileName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("OnlineJava may execute only its independently installed runtime.");
            var temporary = Path.Combine(root, "jvm-tmp"); Directory.CreateDirectory(temporary);
            var environment = spec.EnvironmentOverrides?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            environment["TMPDIR"] = environment["TMP"] = environment["TEMP"] = temporary;
            return spec with { Arguments = new[] { "-XX:-UsePerfData", "-Djava.io.tmpdir=" + temporary }.Concat(spec.Arguments).ToArray(),
                EnvironmentOverrides = environment };
        }
    }

    private static void AssertJava(JavaInstallation java, string root)
    {
        Assert.IsTrue(java.IsUsable, java.ProbeError);
        Assert.AreEqual(21, java.MajorVersion);
        Assert.AreEqual(RuntimeArchitecture.Arm64, java.RuntimeArchitecture);
        Assert.IsTrue(java.ExecutablePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.IsTrue(java.JavaHome.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
    private static void AssertMarker(string final, string manifestSha1, string executable)
    {
        Assert.IsFalse(MojangJavaRuntimeInstaller.IsIncompleteRuntime(final));
        var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(final, MojangJavaRuntimeInstaller.StateFileName)))!;
        Assert.IsTrue(marker["complete"]!.GetValue<bool>());
        Assert.AreEqual(manifestSha1, marker["manifestSha1"]!.GetValue<string>());
        Assert.AreEqual(executable, Path.Combine(final, marker["javaExecutable"]!.GetValue<string>()));
    }
    private static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken ct) =>
        JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
    private static async Task<IReadOnlyList<RuntimeManifestEvidence>> VerifyManifestAsync(string final, JsonObject files, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This gate verifies native macOS Unix file permissions.");
        var evidence = new List<RuntimeManifestEvidence>();
        foreach (var (name, node) in files)
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = node!.AsObject(); var type = descriptor["type"]!.GetValue<string>();
            var path = Path.GetFullPath(Path.Combine(final, name));
            Assert.IsTrue(path.StartsWith(final + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (type == "file")
            {
                Assert.IsNull(new FileInfo(path).LinkTarget);
                var raw = descriptor["downloads"]!["raw"]!;
                var hash = await OnlineRuntimeHttpDownloader.HashFileAsync(path, ct);
                Assert.AreEqual(raw["size"]!.GetValue<long>(), hash.Size, name);
                Assert.AreEqual(raw["sha1"]!.GetValue<string>(), hash.Sha1, name);
                var mode = File.GetUnixFileMode(path);
                var executable = (bool?)descriptor["executable"] == true;
                Assert.AreEqual(executable, (mode & UnixFileMode.UserExecute) != 0, name);
                evidence.Add(new(name, type, raw["url"]!.GetValue<string>(), hash.Size, hash.Sha1, hash.Sha256,
                    mode.ToString(), executable, null, null));
            }
            else if (type == "link")
            {
                var target = descriptor["target"]!.GetValue<string>();
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                Assert.AreEqual(target, info.LinkTarget, name);
                var resolved = info.ResolveLinkTarget(true) ?? throw new InvalidDataException("Unresolved installed runtime link.");
                Assert.IsTrue(resolved.FullName.StartsWith(final + Path.DirectorySeparatorChar, StringComparison.Ordinal), name);
                evidence.Add(new(name, type, null, null, null, null, null, null, target, Path.GetRelativePath(final, resolved.FullName)));
            }
            else
            {
                Assert.AreEqual("directory", type); Assert.IsTrue(Directory.Exists(path), name);
                evidence.Add(new(name, type, null, null, null, null, null, null, null, null));
            }
        }
        return evidence;
    }
}

internal sealed record RuntimeManifestEvidence(string Path, string Type, string? OfficialUrl, long? Size,
    string? Sha1, string? Sha256, string? UnixMode, bool? Executable, string? LinkTarget, string? ResolvedRelativeTarget);

internal sealed class OnlineRuntimeReport(string root)
{
    public string Status { get; set; } = "STARTED";
    public string Stage { get; set; } = "Initializing";
    public string TestRoot { get; } = root;
    public string EvidenceBoundary { get; } = "Production MojangJavaRuntimeInstaller + JavaDiscovery/JavaRuntimeProbe + real ProcessRunner with test-only JVM tmp/UsePerfData isolation; explicit test-only HTTPS hash transport. Runtime install/probe proof, not application downloader, final bundle, Minecraft menu, login or server-join proof.";
    public string Component { get; } = "java-runtime-delta";
    public int RequiredMajor { get; } = 21;
    public string Platform { get; } = "mac-os-arm64";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorType { get; set; }
    public string? CatalogVersion { get; set; }
    public string? ManifestUrl { get; set; }
    public string? ManifestSha1 { get; set; }
    public string? ManifestSha256 { get; set; }
    public long DeclaredRawBytes { get; set; }
    public long TotalNetworkBytes { get; set; }
    public int RegularFileCount { get; set; }
    public int LinkCount { get; set; }
    public long? ReusePayloadNetworkBytes { get; set; }
    public bool ReuseCatalogFresh { get; set; }
    public bool ReuseManifestCacheVerified { get; set; }
    public JavaInstallation? FirstProbe { get; set; }
    public JavaInstallation? IndependentFinalProbe { get; set; }
    public JavaInstallation? SecondProbe { get; set; }
    public IReadOnlyList<JavaInstallation> Discovered { get; set; } = [];
    public IReadOnlyList<RuntimeManifestEvidence> ManifestEntries { get; set; } = [];
    public IReadOnlyList<RuntimeHttpReceipt> Receipts { get; set; } = [];
    public IReadOnlyList<RuntimeHttpFailure> FailedRequests { get; set; } = [];
}
