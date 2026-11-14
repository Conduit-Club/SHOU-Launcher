using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DirectoryPublicationTests
{
    [TestMethod]
    public async Task ExistingDestinationIsNeverReplaced()
    {
        var root = TestFiles.Root(); var source = Path.Combine(root, "stage"); var target = Path.Combine(root, "published");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "keep"), "keep");
        await Assert.ThrowsExactlyAsync<IOException>(() => DirectoryPublication.PublishAsync(source, target, default));
        Assert.IsTrue(Directory.Exists(source)); Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(target, "keep")));
    }
    [TestMethod]
    public async Task WindowsTemporaryDirectoryHandleIsRetriedWithoutCopyingOrOverwriting()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows directory sharing semantics.");
        var root = TestFiles.Root(); var source = Path.Combine(root, "stage"); var target = Path.Combine(root, "published"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "payload"), "fixture");
        using var handle = Hold(source); var pending = DirectoryPublication.PublishAsync(source, target, default);
        Assert.IsFalse(pending.IsCompleted); Assert.IsFalse(Directory.Exists(target)); handle.Dispose(); await pending;
        Assert.IsFalse(Directory.Exists(source)); Assert.AreEqual("fixture", await File.ReadAllTextAsync(Path.Combine(target, "payload")));
    }
    [TestMethod]
    public async Task WindowsDirectoryWaitCanBeCancelledWithoutPublishing()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows directory sharing semantics.");
        var root = TestFiles.Root(); var source = Path.Combine(root, "stage"); var target = Path.Combine(root, "published"); Directory.CreateDirectory(source);
        using var handle = Hold(source); using var cancellation = new CancellationTokenSource();
        var pending = DirectoryPublication.PublishAsync(source, target, cancellation.Token); Assert.IsFalse(pending.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending); Assert.IsTrue(Directory.Exists(source)); Assert.IsFalse(Directory.Exists(target));
    }
    private static SafeFileHandle Hold(string path)
    {
        var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert.IsFalse(handle.IsInvalid, "Test must hold an actual directory handle."); return handle;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
}
