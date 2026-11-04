using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class FileSystemScopeTests
{
    [TestMethod]
    [TestCategory("FileSystemScope")]
    public void ExistingCaseAliasesUseStoredIdentityWhileDistinctDirectoriesStayDistinct()
    {
        var fixture = new PlatformFixture();
        var root = fixture.Child("MiXeD Existing Scope");
        var variant = fixture.Child("mIxEd eXISTING sCOPE");
        Directory.CreateDirectory(root);
        var first = Paths(root);
        if (Directory.Exists(variant))
        {
            var second = Paths(variant);
            Assert.AreEqual(first.CredentialScope, second.CredentialScope);
            if (OperatingSystem.IsMacOS())
            {
                Assert.AreEqual(first.DataDirectory, second.DataDirectory);
                Assert.AreEqual(first.LockFile, second.LockFile);
            }
        }
        else
        {
            // On a case-sensitive volume these are two actual directories, not aliases.
            Directory.CreateDirectory(variant);
            Assert.AreNotEqual(first.CredentialScope, Paths(variant).CredentialScope);
        }
        var other = fixture.Child("Different Actual Scope"); Directory.CreateDirectory(other);
        Assert.AreNotEqual(first.CredentialScope, Paths(other).CredentialScope);
    }

    [TestMethod]
    [TestCategory("FileSystemScope")]
    public void UnicodeAliasesFollowFilesystemIdentityRatherThanGlobalNormalization()
    {
        var fixture = new PlatformFixture();
        foreach (var name in new[] { "caf\u00e9 Existing", "cafe\u0301 Second", "\u212b Angstrom" })
        {
            var root = fixture.Child(name); Directory.CreateDirectory(root);
            var first = Paths(root);
            foreach (var variant in new[] { root.Normalize(NormalizationForm.FormC), root.Normalize(NormalizationForm.FormD) }.Distinct(StringComparer.Ordinal))
            {
                if (Directory.Exists(variant))
                {
                    Assert.AreEqual(first.CredentialScope, Paths(variant).CredentialScope);
                    if (OperatingSystem.IsMacOS()) Assert.AreEqual(first.DataDirectory, Paths(variant).DataDirectory);
                }
                else
                {
                    Directory.CreateDirectory(variant);
                    Assert.AreNotEqual(first.CredentialScope, Paths(variant).CredentialScope);
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("FileSystemScope")]
    public void NewRootScopeIsStableAcrossFirstCreationAndAncestorAliases()
    {
        var fixture = new PlatformFixture();
        var anchor = fixture.Child("MiXeD caf\u00e9 Anchor"); Directory.CreateDirectory(anchor);
        var alias = fixture.Child("mIxEd CAF\u00c9 aNCHOR");
        if (!Directory.Exists(alias)) alias = anchor; // Distinct spelling on a sensitive volume is not an alias.
        foreach (var name in new[] { "MiXeD New Root", "caf\u00e9 NFC", "cafe\u0301 NFD", "\u212b Angstrom", "\u00c5 Ring" })
        {
            var proposed = Path.Combine(alias, name, "new child");
            var before = Paths(proposed);
            Assert.IsFalse(Directory.Exists(proposed), "Path calculation must not create the new root.");
            var scope = before.CredentialScope;
            Directory.CreateDirectory(before.DataDirectory);
            var after = Paths(proposed);
            Assert.AreEqual(scope, after.CredentialScope, "The first creation must not strand its Keychain scope.");
            Assert.AreEqual(before.DataDirectory, after.DataDirectory);
            Assert.AreEqual(before.LockFile, after.LockFile);
        }
    }

    [TestMethod]
    [TestCategory("FileSystemScope")]
    public void CaseSensitiveFutureNamesAreNotUnconditionallyFolded()
    {
        var fixture = new PlatformFixture();
        // No filesystem object exists at either future name yet. Their distinct spelling must be retained.
        var upper = Paths(fixture.Child("FutureFoo"));
        var lower = Paths(fixture.Child("Futurefoo"));
        if (!OperatingSystem.IsWindows()) Assert.AreNotEqual(upper.CredentialScope, lower.CredentialScope);
        Assert.IsFalse(Directory.Exists(upper.DataDirectory));
        Assert.IsFalse(Directory.Exists(lower.DataDirectory));
    }

    private static AppPaths Paths(string root) => new(new PlatformOptions(root), new NativePlatformInfo().Context);
}
