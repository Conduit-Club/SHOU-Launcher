using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ScreenshotTrashTests
{
    [TestMethod]
    public async Task MoveRestoreRoundTripRefusesCollisionAndTamperedRecoveryBytes()
    {
        var instance = new GameInstance("fixture", "fixture", "1.21.1", TestFiles.Root());
        var original = Path.Combine(instance.GameDirectory, "screenshots/fixture.png"); Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var bytes = new byte[] { 1, 2, 3, 4 }; await File.WriteAllBytesAsync(original, bytes);
        var receipt = await ScreenshotTrash.MoveAsync(instance, "screenshots/fixture.png");
        Assert.IsFalse(File.Exists(original)); Assert.AreEqual(receipt, (await ScreenshotTrash.ListAsync(instance)).Single());
        await File.WriteAllTextAsync(original, "new same-name screenshot");
        await Assert.ThrowsAsync<IOException>(() => ScreenshotTrash.RestoreAsync(instance, receipt.Id));
        Assert.AreEqual("new same-name screenshot", await File.ReadAllTextAsync(original)); File.Delete(original);
        var trash = Path.Combine(instance.GameDirectory, ".shou-trash/screenshots", receipt.Id, "image.png");
        await File.WriteAllTextAsync(trash, "tampered");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ScreenshotTrash.RestoreAsync(instance, receipt.Id)); Assert.IsTrue(File.Exists(trash));
        await File.WriteAllBytesAsync(trash, bytes); await ScreenshotTrash.RestoreAsync(instance, receipt.Id);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(original)); Assert.IsEmpty(await ScreenshotTrash.ListAsync(instance));
    }
    [TestMethod]
    public async Task CancellationTraversalAndFolderAliasKeepOriginalFiles()
    {
        var instance = new GameInstance("fixture", "fixture", "1.21.1", TestFiles.Root());
        var screenshots = Path.Combine(instance.GameDirectory, "screenshots"); Directory.CreateDirectory(screenshots);
        var original = Path.Combine(screenshots, "fixture.png"); await File.WriteAllTextAsync(original, "original");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScreenshotTrash.MoveAsync(instance, "screenshots/fixture.png", cancellation.Token));
        Assert.AreEqual("original", await File.ReadAllTextAsync(original));
        await Assert.ThrowsAsync<ArgumentException>(() => ScreenshotTrash.MoveAsync(instance, "screenshots/../private.png"));
        await Assert.ThrowsAsync<ArgumentException>(() => ScreenshotTrash.RestoreAsync(instance, "../private"));
        var aliasRoot = Path.Combine(instance.GameDirectory, ".shou-trash"); Directory.CreateSymbolicLink(aliasRoot, screenshots);
        await Assert.ThrowsAsync<IOException>(() => ScreenshotTrash.MoveAsync(instance, "screenshots/fixture.png"));
        Assert.AreEqual("original", await File.ReadAllTextAsync(original)); Assert.IsEmpty(Directory.GetFiles(screenshots, "receipt.json", SearchOption.AllDirectories));
    }
}
