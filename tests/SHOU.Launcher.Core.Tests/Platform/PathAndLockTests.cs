using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Platform;

[TestClass]
public sealed class PathAndLockTests
{
    [TestMethod]
    public void IsolatedPathsAreCompleteAndConstructorDoesNotWrite()
    {
        var fixture = new PlatformFixture();
        var root = fixture.Child("new data 目录");
        var paths = new AppPaths(new PlatformOptions(root), PlatformFixture.Context(LauncherOS.MacOS));
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsTrue(paths.IsIsolated);
        foreach (var path in new[] { paths.DataDirectory, paths.SharedDataDirectory, paths.LocalDataDirectory,
            paths.CacheDirectory, paths.LogsDirectory, paths.RuntimesDirectory, paths.InstancesDirectory, paths.TempDirectory, paths.LockFile })
            Assert.IsTrue(Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar));
        Assert.AreNotEqual(paths.ResourcesDirectory, paths.DataDirectory);
        Assert.AreNotEqual(paths.CredentialScope, new AppPaths(new PlatformOptions(fixture.Child("other")), PlatformFixture.Context(LauncherOS.MacOS)).CredentialScope);
    }

    [TestMethod]
    public void WindowsDefaultsKeepLegacyRootsAndLock()
    {
        var fixture = new PlatformFixture();
        var paths = new AppPaths(new PlatformOptions(), PlatformFixture.Context(LauncherOS.Windows),
            executableDirectory: fixture.Child("application"), homeDirectory: fixture.Root,
            roamingDataDirectory: fixture.Child("roaming"), localDataDirectory: fixture.Child("local"), temporaryDirectory: fixture.Child("temp"));
        Assert.AreEqual(fixture.Child(Path.Combine("application", "PCL")), paths.DataDirectory);
        Assert.AreEqual(fixture.Child(Path.Combine("roaming", "PCLCE")), paths.SharedDataDirectory);
        Assert.AreEqual(fixture.Child(Path.Combine("local", "PCLCE", "instance.lock")), paths.LockFile);
        Assert.IsFalse(Directory.Exists(paths.DataDirectory));
    }

    [TestMethod]
    public void MacDefaultsSeparateResourcesDataCacheAndLogs()
    {
        var fixture = new PlatformFixture();
        var paths = new AppPaths(new PlatformOptions(), PlatformFixture.Context(LauncherOS.MacOS),
            executableDirectory: fixture.Child("readonly.app"), homeDirectory: fixture.Root, temporaryDirectory: fixture.Child("temp"));
        Assert.AreEqual(fixture.Child(Path.Combine("Library", "Application Support", "SHOU-Launcher")), paths.DataDirectory);
        Assert.AreEqual(fixture.Child(Path.Combine("Library", "Caches", "SHOU-Launcher")), paths.CacheDirectory);
        Assert.AreEqual(fixture.Child(Path.Combine("Library", "Logs", "SHOU-Launcher")), paths.LogsDirectory);
        Assert.IsFalse(Directory.Exists(paths.CacheDirectory));
    }

    [TestMethod]
    public void RelativeIsolationRootIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(new PlatformOptions("relative-data"), PlatformFixture.Context(LauncherOS.MacOS)));
    }

    [TestMethod]
    public void DataLeaseExcludesSecondWriterAndAllowsCrashStyleRecovery()
    {
        var fixture = new PlatformFixture();
        var paths = new AppPaths(new PlatformOptions(fixture.Child("locked")), new NativePlatformInfo().Context);
        var locks = new DataDirectoryLock();
        using (var lease = locks.Acquire(paths))
        {
            Assert.Throws<IOException>(() => locks.Acquire(paths));
            Assert.AreEqual(paths.LockFile, lease.LockPath);
        }
        Assert.IsTrue(File.Exists(paths.LockFile), "The inode is retained to avoid deletion races.");
        using (var next = locks.Acquire(paths)) Assert.AreEqual(paths.LockFile, next.LockPath);
        Assert.AreEqual(Environment.ProcessId.ToString(), File.ReadAllText(paths.LockFile));
    }

    [TestMethod]
    public void IndependentIsolationRootsCanHoldLeasesTogether()
    {
        var fixture = new PlatformFixture();
        var context = new NativePlatformInfo().Context;
        var locks = new DataDirectoryLock();
        using var first = locks.Acquire(new AppPaths(new PlatformOptions(fixture.Child("one")), context));
        using var second = locks.Acquire(new AppPaths(new PlatformOptions(fixture.Child("two")), context));
        Assert.AreNotEqual(first.LockPath, second.LockPath);
    }

    [TestMethod]
    public void PlatformInitializationReportsActualProcessWithoutWindowsStaticCalls()
    {
        var context = new NativePlatformInfo().Context;
        Assert.AreNotEqual(LauncherOS.Unknown, context.OS);
        Assert.AreNotEqual(RuntimeArchitecture.Unknown, context.ProcessArchitecture);
        Assert.IsFalse(string.IsNullOrWhiteSpace(context.OSVersion));
        if (OperatingSystem.IsMacOS()) Assert.AreEqual(LauncherOS.MacOS, context.OS);
    }

    [TestMethod]
    public void IsolationRootAliasesShareIdentityAndReservedPathsCannotEscape()
    {
        var fixture = new PlatformFixture();
        var context = new NativePlatformInfo().Context;
        var root = fixture.Child("real");
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows())
        {
            // Windows symlink creation may require policy privileges; casing equivalence is still a real path identity check.
            var one = new AppPaths(new PlatformOptions(root), context);
            var two = new AppPaths(new PlatformOptions(root.ToUpperInvariant()), context);
            Assert.AreEqual(one.CredentialScope, two.CredentialScope);
            return;
        }
        var alias = fixture.Child("alias");
        Directory.CreateSymbolicLink(alias, root);
        var direct = new AppPaths(new PlatformOptions(root), context);
        var linked = new AppPaths(new PlatformOptions(alias), context);
        Assert.AreEqual(direct.CredentialScope, linked.CredentialScope);
        Assert.AreEqual(direct.LockFile, linked.LockFile);
        var outside = fixture.Child("outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "cache"), outside);
        Assert.Throws<IOException>(() => new AppPaths(new PlatformOptions(root), context));
    }
}
