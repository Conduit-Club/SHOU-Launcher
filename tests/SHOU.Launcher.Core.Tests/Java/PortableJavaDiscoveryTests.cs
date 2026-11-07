using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
public sealed class PortableJavaDiscoveryTests
{
    [TestMethod]
    public async Task KeywordSearchFindsPortableJdksAnd32BitJreWithoutTraversingUnrelatedFolders()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root);
        foreach (var name in new[] { "JDK11", "JDK17", "JDK22", "Jre1.8", "Downloads/secret-jdk" })
        { var path = Path.Combine(root, name, "bin"); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "java.exe"), "probe fixture only"); }
        var runner = new ProbeRunner();
        var platform = new PlatformContext(LauncherOS.Windows, RuntimeArchitecture.X64, RuntimeArchitecture.X64, "fixture");
        var discovery = new JavaDiscovery(runner, platform, includeSystemSources: false, additionalSearchRoots: [root, root]);
        var found = await discovery.DiscoverAsync(new(null, Path.Combine(root, "absent")));
        CollectionAssert.AreEquivalent(new[] { 8, 11, 17, 22 }, found.Select(j => j.MajorVersion).ToArray());
        Assert.AreEqual(4, runner.Probes); Assert.IsTrue(found.All(j => j.IsUsable)); Assert.AreEqual(RuntimeArchitecture.X86, found.Single(j => j.MajorVersion == 8).RuntimeArchitecture);
        Assert.AreEqual(22, found[0].MajorVersion); Assert.IsFalse(found.Any(j => j.ExecutablePath.Contains("Downloads", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task PortableDiscoveryCancellationNeverStartsAProbe()
    {
        var runner = new ProbeRunner(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var discovery = new JavaDiscovery(runner, new(LauncherOS.Windows, RuntimeArchitecture.X64, RuntimeArchitecture.X64, "fixture"), includeSystemSources: false, additionalSearchRoots: [TestFiles.Root()]);
        await Assert.ThrowsAsync<OperationCanceledException>(() => discovery.DiscoverAsync(new(null, TestFiles.Root()), cts.Token)); Assert.AreEqual(0, runner.Probes);
    }
    private sealed class ProbeRunner : IProcessRunner
    {
        public int Probes;
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("No game or installer is allowed.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Probes++;
            var directory = Directory.GetParent(Path.GetDirectoryName(spec.FileName)!)!.FullName;
            var name = Path.GetFileName(directory); var version = name.Contains("1.8", StringComparison.Ordinal) ? "1.8.0_472" : name.Replace("JDK", "", StringComparison.Ordinal) + ".0.1";
            Assert.IsTrue(spec.Arguments.Contains("-version"));
            return Task.FromResult(new ProcessResult(0, "", $"java.home = {directory}\njava.version = {version}\njava.runtime.version = {version}\njava.vendor = Fixture\nos.arch = {(name.Contains("1.8", StringComparison.Ordinal) ? "x86" : "amd64")}"));
        }
    }
}
