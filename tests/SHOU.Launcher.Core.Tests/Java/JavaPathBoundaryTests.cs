using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Tests.Platform;

namespace SHOU.Launcher.Core.Tests.Java;

[TestClass]
[TestCategory("JavaPathBoundary")]
public sealed class JavaPathBoundaryTests
{
    [TestMethod]
    public void CanonicalStringBoundaryDoesNotFoldPosixNamesOrAcceptAdjacentPrefixes()
    {
        var anchor = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "SHOU-PURE-BOUNDARY");
        var root = Path.Combine(anchor, "home"); var differentCase = Path.Combine(anchor, "Home", "file");
        Assert.IsTrue(JavaPathBoundary.ContainsCanonical(root, root));
        Assert.IsTrue(JavaPathBoundary.ContainsCanonical(root, Path.Combine(root, "file")));
        Assert.IsFalse(JavaPathBoundary.ContainsCanonical(root, Path.Combine(anchor, "home-other", "file")));
        Assert.AreEqual(OperatingSystem.IsWindows(), JavaPathBoundary.ContainsCanonical(root, differentCase),
            "This is a pure comparison fixture, not evidence of a case-sensitive volume or Windows execution.");
        Assert.AreEqual("child/file", JavaPathBoundary.RelativeCanonical(root, Path.Combine(root, "child", "file")));
        Assert.Throws<InvalidDataException>(() => JavaPathBoundary.RelativeCanonical(root, Path.Combine(anchor, "home-other", "file")));
        if (!OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => JavaPathBoundary.RelativeCanonical(root, differentCase));
    }

    [TestMethod]
    public void ExistingFilesystemSpellingAndNewDescendantsUseObservedIdentity()
    {
        var f = new PlatformFixture(); var root = f.Child("ActualCase-é"); var alias = f.Child("ACTUALCASE-É");
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "identity"), "owned fixture");
        var aliasExists = File.Exists(Path.Combine(alias, "identity"));
        Console.WriteLine("CURRENT_FS_CASE_ALIAS=" + aliasExists + "; no case-sensitive test volume was created");
        if (aliasExists)
        {
            Assert.IsTrue(JavaPathBoundary.Contains(root, Path.Combine(alias, "identity")));
            Assert.AreEqual("identity", JavaPathBoundary.Relative(root, Path.Combine(alias, "identity")));
            var pending = Path.Combine(root, "new-child");
            var before = JavaPathBoundary.Canonical(Path.Combine(alias, "new-child"));
            Directory.CreateDirectory(pending);
            Assert.IsTrue(JavaPathBoundary.ContainsCanonical(before, JavaPathBoundary.Canonical(pending)));
            Assert.IsTrue(JavaPathBoundary.ContainsCanonical(JavaPathBoundary.Canonical(pending), before));
        }
        else
        {
            Directory.CreateDirectory(alias);
            Assert.IsFalse(JavaPathBoundary.Contains(root, alias), "Distinct objects are not merged by a machine-wide lowercase rule.");
        }
    }

    [TestMethod]
    [TestCategory("JavaTrustPosixLinks")]
    public void RealInternalAndAdjacentEscapingLinksUsePhysicalBoundaryButRetainLiteralOwnership()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("POSIX links need a separate native Windows junction gate.");
        var f = new PlatformFixture(); var root = f.Child("home"); var sibling = f.Child("home-other");
        Directory.CreateDirectory(root); Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(root, "inside"), "inside"); File.WriteAllText(Path.Combine(sibling, "outside"), "outside");
        File.CreateSymbolicLink(Path.Combine(root, "internal"), "inside");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), "../home-other");
        Assert.IsTrue(JavaPathBoundary.Contains(root, Path.Combine(root, "internal")));
        Assert.AreEqual("internal", JavaPathBoundary.Relative(root, Path.Combine(root, "internal")), "Entry names are hashed without replacing link names with their target.");
        Assert.IsFalse(JavaPathBoundary.Contains(root, Path.Combine(root, "escape", "outside")));
        Assert.IsTrue(JavaPathBoundary.ContainsLiteral(root, Path.Combine(root, "escape", "outside")), "Escaped owned paths cannot be relabeled as ordinary external Java.");
        Assert.Throws<InvalidDataException>(() => JavaPathBoundary.Relative(root, Path.Combine(root, "escape", "outside")));
    }

    [TestMethod]
    public async Task ExistingVariantAliasesNeverRecursivelyDeriveAndOnlyReuseObservedSameObject()
    {
        var f = new PlatformFixture(); var sourceHome = f.Child("source-home");
        var bin = Path.Combine(sourceHome, "bin"); Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(executable, "explicit unit probe fixture");
        var security = Path.Combine(sourceHome, "lib", "security"); Directory.CreateDirectory(security);
        File.WriteAllBytes(Path.Combine(security, "cacerts"), JavaTrustKernelTests.Jks(2, [("root", JavaTrustKernelTests.RootCertificate())]));
        var source = new JavaInstallation(executable, sourceHome, "21.0.13", 21, "explicit fake", RuntimeArchitecture.Arm64, "fixture");
        var root = f.Child("derived-trust"); var service = new DerivedJavaTrustService(new EchoDiscovery());
        var result = await service.CreateRepairedCopyAsync(new(source, root));
        var variantName = Path.GetFileName(Path.GetDirectoryName(result.Java.JavaHome)!);
        var aliasHome = Path.Combine(f.Child("DERIVED-TRUST"), variantName, "home");
        if (Directory.Exists(aliasHome))
        {
            var aliasJava = result.Java with { JavaHome = aliasHome, ExecutablePath = Path.Combine(aliasHome, "bin", Path.GetFileName(executable)) };
            var reused = await service.CreateRepairedCopyAsync(new(aliasJava, root));
            Assert.AreEqual(result.Java.ExecutablePath, reused.Java.ExecutablePath);
            Assert.AreEqual(1, Directory.GetDirectories(root).Count(path => !Path.GetFileName(path).StartsWith('.')));
            Assert.IsTrue(await DerivedJavaTrustService.ValidateCandidateReadOnlyAsync(root, aliasJava.ExecutablePath));
        }
        else Assert.IsFalse(JavaPathBoundary.Contains(root, aliasHome), "Current filesystem treats this spelling as a distinct missing object; no alias reuse is claimed.");
    }

    private sealed class EchoDiscovery : IJavaDiscovery
    {
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JavaInstallation(executable, Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!,
                "21.0.13", 21, "explicit fake", RuntimeArchitecture.Arm64, "fixture"));
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
