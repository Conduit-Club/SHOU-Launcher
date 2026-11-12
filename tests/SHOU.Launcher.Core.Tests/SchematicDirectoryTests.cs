using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class SchematicDirectoryTests
{
    [TestMethod]
    public async Task BrowsingStaysInTheSelectedDirectoryAndDoesNotFollowLinks()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Local browsing stays offline.")));
        var downloader = new VerifiedDownloader(http); await using var service = new LauncherService(PlatformServicesFactory.Create(new(TestFiles.Root()), downloader), http, downloader, new());
        var instance = await service.CreateInstanceAsync("folders", "1.21.1"); var root = Path.Combine(instance.GameDirectory, "schematics");
        Directory.CreateDirectory(Path.Combine(root, "builds/nested")); await File.WriteAllTextAsync(Path.Combine(root, "builds/a.litematic"), "nested"); await File.WriteAllTextAsync(Path.Combine(root, "a.litematic"), "root"); await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "unrelated");
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), TestFiles.Root());
        var top = await service.BrowseInstanceContentAsync(instance.Id, "schematics", ""); Assert.HasCount(2, top); Assert.IsTrue(top.Single(r => r.Name == "builds").Directory);
        var rows = await service.BrowseInstanceContentAsync(instance.Id, "schematics", "builds"); Assert.HasCount(2, rows); Assert.AreEqual("schematics/builds/a.litematic", rows.Single(r => !r.Directory).RelativePath);
        Assert.AreEqual(Path.Combine(root, "builds"), await service.GetInstanceContentDirectoryAsync(instance.Id, "schematics", "builds"));
        await Assert.ThrowsAsync<IOException>(() => service.BrowseInstanceContentAsync(instance.Id, "schematics", "alias"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.BrowseInstanceContentAsync(instance.Id, "schematics", "../mods"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.BrowseInstanceContentAsync(instance.Id, "mods", ""));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => service.BrowseInstanceContentAsync(instance.Id, "schematics", "", cancel.Token));
    }
    [TestMethod]
    public async Task NestedLocalImportTrashAndRestorePreserveTheRootFileAndOriginalPath()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Local management stays offline.")));
        var downloader = new VerifiedDownloader(http); await using var service = new LauncherService(PlatformServicesFactory.Create(new(TestFiles.Root()), downloader), http, downloader, new());
        var instance = await service.CreateInstanceAsync("manage", "1.21.1"); var root = Path.Combine(instance.GameDirectory, "schematics"); Directory.CreateDirectory(Path.Combine(root, "builds/nested"));
        var original = Path.Combine(root, "a.litematic"); await File.WriteAllTextAsync(original, "root original"); var source = Path.Combine(TestFiles.Root(), "a.litematic"); await File.WriteAllTextAsync(source, "nested original");
        await service.ImportInstanceContentToDirectoryAsync(instance.Id, "schematics", "builds/nested", source); var target = Path.Combine(root, "builds/nested/a.litematic"); Assert.AreEqual("nested original", await File.ReadAllTextAsync(target));
        await Assert.ThrowsAsync<IOException>(() => service.ImportInstanceContentToDirectoryAsync(instance.Id, "schematics", "builds/nested", source));
        var receipt = await service.TrashInstanceContentAsync(instance.Id, "schematics", ["schematics/builds/nested/a.litematic"]); Assert.IsFalse(File.Exists(target));
        Directory.Delete(Path.GetDirectoryName(target)!); await service.RestoreInstanceContentAsync(instance.Id, "schematics", receipt.Id);
        Assert.AreEqual("nested original", await File.ReadAllTextAsync(target)); Assert.AreEqual("root original", await File.ReadAllTextAsync(original)); Assert.AreEqual("nested original", await File.ReadAllTextAsync(source));
        Assert.IsFalse(File.Exists(Path.Combine(instance.GameDirectory, ".shou-content-manifest.json")));
    }
    [TestMethod]
    public async Task MissingOrLinkedDestinationsNeverFallBackToTheRootAndLateLinksPreventRestore()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Local management stays offline.")));
        var downloader = new VerifiedDownloader(http); await using var service = new LauncherService(PlatformServicesFactory.Create(new(TestFiles.Root()), downloader), http, downloader, new());
        var instance = await service.CreateInstanceAsync("boundaries", "1.21.1"); var root = Path.Combine(instance.GameDirectory, "schematics"); Directory.CreateDirectory(Path.Combine(root, "builds"));
        var source = Path.Combine(TestFiles.Root(), "sample.schem"); await File.WriteAllTextAsync(source, "unchanged source");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.ImportInstanceContentToDirectoryAsync(instance.Id, "schematics", "missing", source));
        Assert.IsFalse(File.Exists(Path.Combine(root, "sample.schem"))); Assert.IsFalse(Directory.Exists(Path.Combine(root, "missing")));
        await service.ImportInstanceContentToDirectoryAsync(instance.Id, "schematics", "builds", source); var receipt = await service.TrashInstanceContentAsync(instance.Id, "schematics", ["schematics/builds/sample.schem"]);
        Directory.Delete(Path.Combine(root, "builds")); var outside = TestFiles.Root(); Directory.CreateSymbolicLink(Path.Combine(root, "builds"), outside);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreInstanceContentAsync(instance.Id, "schematics", receipt.Id));
        await Assert.ThrowsAsync<IOException>(() => service.ImportInstanceContentToDirectoryAsync(instance.Id, "schematics", "builds", source));
        Assert.IsFalse(File.Exists(Path.Combine(outside, "sample.schem")));
        // Existing receipt enumeration withholds unsafe destinations until the link is removed.
        Directory.Delete(Path.Combine(root, "builds")); Directory.CreateDirectory(Path.Combine(root, "builds"));
        Assert.HasCount(1, await service.GetInstanceContentTrashAsync(instance.Id, "schematics"));
        await service.RestoreInstanceContentAsync(instance.Id, "schematics", receipt.Id);
        Assert.AreEqual("unchanged source", await File.ReadAllTextAsync(Path.Combine(root, "builds/sample.schem")));
    }
}
