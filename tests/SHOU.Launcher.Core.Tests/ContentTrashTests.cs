using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ContentTrashTests
{
    private static GameInstance Instance() => new("fixture", "Fixture", "1.21.1", TestFiles.Root());
    private static async Task<ContentInstalledFile> FileFor(GameInstance i, string kind, string name)
    {
        var folder = kind == "shaders" ? "shaderpacks" : kind; Directory.CreateDirectory(Path.Combine(i.GameDirectory, folder));
        var bytes = System.Text.Encoding.UTF8.GetBytes("fixture: " + name); await File.WriteAllBytesAsync(Path.Combine(i.GameDirectory, folder, name), bytes);
        return new("project-" + name, "release", kind == "shaders" ? "shader" : kind == "resourcepacks" ? "resourcepack" : "schematic", folder + "/" + name, Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.Length, Client: "optional");
    }
    [TestMethod]
    [DataRow("resourcepacks", "one.zip")]
    [DataRow("shaders", "one.zip")]
    [DataRow("schematics", "one.litematic")]
    public async Task PublicServiceRoundTripSurvivesRestartAndMergesLaterInstalledFiles(string kind, string name)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        var i = new GameInstance("fixture", "Fixture", "1.21.1", Path.Combine(platform.Paths.InstancesDirectory, "fixture")); var a = await FileFor(i, kind, name); var b = await FileFor(i, kind, "two" + Path.GetExtension(name));
        await File.WriteAllBytesAsync(Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(new(1, i.Id, [a, b])));
        var config = Path.Combine(i.GameDirectory, "options.txt"); await File.WriteAllTextAsync(config, "user selection remains\n"); var configBytes = await File.ReadAllBytesAsync(config);
        await using (var store = new ConfigurationStore(platform.Paths)) await store.SaveAsync(new(new(), [i], []));
        InstanceModTrash removed;
        await using (var service = new LauncherService(platform, http, downloader, new()))
        { await service.InitializeAsync(); removed = await service.TrashInstanceContentAsync(i.Id, kind, [a.RelativePath]); Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, a.RelativePath))); }
        var later = await FileFor(i, kind, "later" + Path.GetExtension(name));
        await File.WriteAllBytesAsync(Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(new(1, i.Id, [b, later])));
        await using (var restarted = new LauncherService(platform, http, downloader, new()))
        {
            await restarted.InitializeAsync(); Assert.AreEqual(removed.Id, (await restarted.GetInstanceContentTrashAsync(i.Id, kind)).Single().Id);
            await restarted.RestoreInstanceContentAsync(i.Id, kind, removed.Id); Assert.IsEmpty(await restarted.GetInstanceContentTrashAsync(i.Id, kind));
        }
        var manifest = await ContentInstallFiles.ReadManifestAsync(i); Assert.HasCount(3, manifest.Files); Assert.AreEqual("optional", manifest.Files.Single(f => f.RelativePath == a.RelativePath).Client);
        Assert.IsTrue(await ContentInstallFiles.MatchesAsync(Path.Combine(i.GameDirectory, a.RelativePath), a.Sha1, a.Size, default)); CollectionAssert.AreEqual(configBytes, await File.ReadAllBytesAsync(config));
    }
    [TestMethod]
    [DataRow("resourcepacks")]
    [DataRow("shaders")]
    public async Task UntrackedResourcesDoNotCreateAManifestAndMissingFolderCanBeRestored(string kind)
    {
        var i = Instance(); var a = await FileFor(i, kind, "one.zip"); var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default, kind);
        Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName))); Directory.Delete(Path.GetDirectoryName(Path.Combine(i.GameDirectory, a.RelativePath))!);
        await ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default, kind);
        Assert.IsTrue(await ContentInstallFiles.MatchesAsync(Path.Combine(i.GameDirectory, a.RelativePath), a.Sha1, a.Size, default)); Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName)));
    }
    [TestMethod]
    [DataRow("resourcepacks")]
    [DataRow("shaders")]
    [DataRow("schematics")]
    public async Task CancelledTrashAndRestoreRollBackAndKeepARecoverableReceipt(string kind)
    {
        var i = Instance(); var extension = kind == "schematics" ? ".litematic" : ".zip"; var a = await FileFor(i, kind, "one" + extension); var b = await FileFor(i, kind, "two" + extension); var paths = new[] { a.RelativePath, b.RelativePath }; var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModFileManagement.TrashAsync(i, paths, () => { if (++calls == 2) throw new OperationCanceledException(); }, default, kind));
        Assert.IsTrue(paths.All(p => File.Exists(Path.Combine(i.GameDirectory, p)))); Assert.IsEmpty(await ModFileManagement.ListAsync(i, default, kind));
        var receipt = await ModFileManagement.TrashAsync(i, paths, () => { }, default, kind); calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModFileManagement.RestoreAsync(i, receipt.Id, () => { if (++calls == 3) throw new OperationCanceledException(); }, default, kind));
        Assert.IsTrue(paths.All(p => !File.Exists(Path.Combine(i.GameDirectory, p)))); Assert.HasCount(1, await ModFileManagement.ListAsync(i, default, kind));
        await ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default, kind); Assert.IsTrue(paths.All(p => File.Exists(Path.Combine(i.GameDirectory, p))));
    }
    [TestMethod]
    [DataRow("collision")]
    [DataRow("changed")]
    [DataRow("receipt")]
    [DataRow("link")]
    public async Task RestoreNeverOverwritesExternalFilesOrAcceptsChangedOrEscapingTrash(string mode)
    {
        var i = Instance(); var a = await FileFor(i, "resourcepacks", "one.zip"); var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default, "resourcepacks"); var root = Path.Combine(i.GameDirectory, ".shou-content-trash/resourcepacks", receipt.Id); var payload = Path.Combine(root, a.RelativePath);
        if (mode == "collision") await File.WriteAllTextAsync(Path.Combine(i.GameDirectory, a.RelativePath), "external");
        if (mode == "changed") await File.WriteAllTextAsync(payload, "changed");
        if (mode == "receipt") { var path = Path.Combine(root, "receipt.json"); var record = JsonNode.Parse(await File.ReadAllTextAsync(path))!; record["Files"]![0]!["RelativePath"] = "../escape.zip"; await File.WriteAllTextAsync(path, record.ToJsonString()); }
        if (mode == "link") { var other = Path.Combine(TestFiles.Root(), "outside.zip"); await File.WriteAllTextAsync(other, "external"); File.Move(payload, payload + ".saved"); File.CreateSymbolicLink(payload, other); }
        try { await ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default, "resourcepacks"); Assert.Fail("Unsafe restore must fail."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        if (mode == "collision") Assert.AreEqual("external", await File.ReadAllTextAsync(Path.Combine(i.GameDirectory, a.RelativePath))); else Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, a.RelativePath)));
    }
    [TestMethod]
    public async Task CategoriesAndInstancesCannotUseEachOthersReceiptAndPathsAreNotGuessed()
    {
        var i = Instance(); var a = await FileFor(i, "resourcepacks", "one.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default, "shaders"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModFileManagement.TrashAsync(i, [a.RelativePath, a.RelativePath.ToUpperInvariant()], () => { }, default, "resourcepacks"));
        var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default, "resourcepacks");
        await Assert.ThrowsAsync<InvalidDataException>(() => ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default, "shaders"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModFileManagement.RestoreAsync(i with { Id = "other" }, receipt.Id, () => { }, default, "resourcepacks"));
        Assert.HasCount(1, await ModFileManagement.ListAsync(i, default, "resourcepacks"));
    }
}
