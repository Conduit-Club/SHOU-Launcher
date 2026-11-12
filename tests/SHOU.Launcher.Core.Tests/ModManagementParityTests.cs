using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ModManagementParityTests
{
    private static GameInstance Instance()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "mods"));
        return new("fixture", "Fixture", "1.21.1", root, Loader: "fabric", GameVersionId: "1.21.1");
    }
    private static async Task<ContentInstalledFile> Mod(GameInstance i, string name)
    {
        var bytes = Encoding.UTF8.GetBytes("fixture " + name); await File.WriteAllBytesAsync(Path.Combine(i.GameDirectory, "mods", name), bytes);
        return new("fixture", "1", "mod", "mods/" + name, Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.Length);
    }
    [TestMethod]
    public async Task BulkEnableDisableAndMidwayFailurePreserveEveryOriginal()
    {
        var i = Instance(); await Mod(i, "one.jar"); await Mod(i, "two.jar"); var paths = new[] { "mods/one.jar", "mods/two.jar" }; var calls = 0;
        Assert.Throws<IOException>(() => ModFileManagement.Toggle(i, paths, false, () => { if (++calls == 2) throw new IOException("fixture"); }, default));
        Assert.IsTrue(paths.All(p => File.Exists(Path.Combine(i.GameDirectory, p))));
        ModFileManagement.Toggle(i, paths, false, () => { }, default); Assert.IsTrue(InstanceWorkspace.List(i, "mods").All(r => !r.Enabled));
        ModFileManagement.Toggle(i, paths.Select(p => p + ".disabled").ToArray(), true, () => { }, default); Assert.IsTrue(InstanceWorkspace.List(i, "mods").All(r => r.Enabled));
    }
    [TestMethod]
    public async Task TrashAndRestartRestoreMergeManifestAndPreserveOptionalRoles()
    {
        var i = Instance(); var a = (await Mod(i, "one.jar")) with { Client = "optional" }; var b = await Mod(i, "two.jar");
        await File.WriteAllBytesAsync(Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(new(1, i.Id, [a, b])));
        var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default);
        Assert.HasCount(1, InstanceWorkspace.List(i, "mods")); Assert.HasCount(1, (await ContentInstallFiles.ReadManifestAsync(i)).Files);
        Assert.AreEqual(receipt.Id, (await ModFileManagement.ListAsync(i, default)).Single().Id);
        await ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default);
        Assert.HasCount(2, InstanceWorkspace.List(i, "mods")); Assert.AreEqual("optional", (await ContentInstallFiles.ReadManifestAsync(i)).Files.Single(f => f.RelativePath == a.RelativePath).Client);
        Assert.HasCount(0, await ModFileManagement.ListAsync(i, default));
    }
    [TestMethod]
    public async Task TrashCancellationRollsBackAndRestoreCollisionNeverOverwrites()
    {
        var i = Instance(); var a = await Mod(i, "one.jar"); await Mod(i, "two.jar"); var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => ModFileManagement.TrashAsync(i, [a.RelativePath, "mods/two.jar"], () => { if (++calls == 2) throw new IOException("fixture"); }, default));
        Assert.HasCount(2, InstanceWorkspace.List(i, "mods"));
        var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default);
        await File.WriteAllTextAsync(Path.Combine(i.GameDirectory, a.RelativePath), "external");
        await Assert.ThrowsAsync<IOException>(() => ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default));
        Assert.AreEqual("external", await File.ReadAllTextAsync(Path.Combine(i.GameDirectory, a.RelativePath))); Assert.HasCount(1, await ModFileManagement.ListAsync(i, default));
    }
    [TestMethod]
    public async Task ChangedTrashBytesCannotBeRestored()
    {
        var i = Instance(); var a = await Mod(i, "one.jar"); var receipt = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default);
        await File.WriteAllTextAsync(Path.Combine(i.GameDirectory, ".shou-mod-trash", receipt.Id, a.RelativePath), "changed");
        await Assert.ThrowsAsync<IOException>(() => ModFileManagement.RestoreAsync(i, receipt.Id, () => { }, default)); Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, a.RelativePath)));
    }
    [TestMethod]
    public async Task OldAndDisabledImportsAreEnabledLikeWpfWithoutChangingTheSource()
    {
        var i = Instance(); var source = Path.Combine(TestFiles.Root(), "sample.jar.old"); await File.WriteAllTextAsync(source, "public");
        await InstanceWorkspace.ImportAsync(i, "mods", source, default); Assert.IsTrue(InstanceWorkspace.List(i, "mods").Single().Enabled); Assert.IsTrue(File.Exists(source)); Assert.IsTrue(File.Exists(Path.Combine(i.GameDirectory, "mods/sample.jar")));
    }
    [TestMethod]
    public async Task LocalReplacementBacksUpExactManifestAndDropsStaleOnlineIdentity()
    {
        var i = Instance(); var old = await Mod(i, "replace.jar"); var path = Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName);
        var before = ContentInstallFiles.SerializeManifest(new(1, i.Id, [old])); await File.WriteAllBytesAsync(path, before);
        var source = Path.Combine(TestFiles.Root(), "replace.jar"); await File.WriteAllTextAsync(source, "replacement");
        await InstanceWorkspace.ImportAsync(i, "mods", source, default);
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(Path.Combine(i.GameDirectory, old.RelativePath))); Assert.HasCount(0, (await ContentInstallFiles.ReadManifestAsync(i)).Files);
        var backup = Directory.GetDirectories(Path.Combine(i.GameDirectory, ".shou-mod-backups")).Single();
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Path.Combine(backup, ContentInstallFiles.ManifestName))); Assert.AreEqual("fixture replace.jar", await File.ReadAllTextAsync(Path.Combine(backup, old.RelativePath)));
    }
    [TestMethod]
    public async Task CancelledLocalImportAndResourceCollisionLeaveTargetsUnchanged()
    {
        var i = Instance(); var source = Path.Combine(TestFiles.Root(), "replace.jar"); await File.WriteAllTextAsync(source, "replacement"); await Mod(i, "replace.jar"); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => InstanceWorkspace.ImportAsync(i, "mods", source, ct.Token)); Assert.AreEqual("fixture replace.jar", await File.ReadAllTextAsync(Path.Combine(i.GameDirectory, "mods/replace.jar")));
        Directory.CreateDirectory(Path.Combine(i.GameDirectory, "resourcepacks")); var zip = Path.Combine(TestFiles.Root(), "pack.zip"); await File.WriteAllTextAsync(zip, "new"); await File.WriteAllTextAsync(Path.Combine(i.GameDirectory, "resourcepacks/pack.zip"), "old");
        await Assert.ThrowsAsync<IOException>(() => InstanceWorkspace.ImportAsync(i, "resourcepacks", zip, default)); Assert.AreEqual("old", await File.ReadAllTextAsync(Path.Combine(i.GameDirectory, "resourcepacks/pack.zip")));
    }
    [TestMethod]
    public void MoreThanTwoThousandFilesAreNotSilentlyTruncatedAndCancellationWorks()
    {
        var i = Instance(); for (var n = 0; n < 2001; n++) File.WriteAllBytes(Path.Combine(i.GameDirectory, "mods", n + ".jar"), []);
        Assert.HasCount(2001, InstanceWorkspace.List(i, "mods")); using var ct = new CancellationTokenSource(); ct.Cancel(); Assert.Throws<OperationCanceledException>(() => InstanceWorkspace.List(i, "mods", ct.Token));
    }
    [TestMethod]
    public void DependencyAndEmbeddedMetadataAreReadWithoutExtraction()
    {
        var i = Instance(); using (var zip = ZipFile.Open(Path.Combine(i.GameDirectory, "mods/fixture.jar"), ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open())) writer.Write("{\"id\":\"outer\",\"name\":\"Outer\",\"depends\":{\"minecraft\":\"1.21.1\",\"missing\":\"*\"},\"jars\":[{\"file\":\"nested.jar\"}]}");
            using var bytes = new MemoryStream(); using (var nested = new ZipArchive(bytes, ZipArchiveMode.Create, true)) using (var writer = new StreamWriter(nested.CreateEntry("fabric.mod.json").Open())) writer.Write("{\"id\":\"inner\",\"version\":\"1\"}");
            using var output = zip.CreateEntry("nested.jar").Open(); output.Write(bytes.ToArray());
        }
        var row = InstanceWorkspace.List(i, "mods").Single(); Assert.AreEqual("outer", row.ModId); CollectionAssert.Contains(row.Dependencies!.ToArray(), "missing"); Assert.AreEqual("inner 1", row.EmbeddedMods!.Single()); Assert.IsNull(row.MetadataError);
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(i.GameDirectory, "mods")));
    }
    [TestMethod]
    [DataRow("", 1540447798u)]
    [DataRow("abc", 1621425345u)]
    [DataRow("abc def\n", 455443312u)]
    public async Task CurseForgeFingerprintMatchesWpfVectors(string value, uint expected)
    { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value)); Assert.AreEqual(expected, await CurseForgeFingerprint.ComputeAsync(stream, default)); }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurseForgeBatchFingerprintsUseOfficialApiAndRejectMismatchedIdentity(bool mismatch)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Assert.AreEqual("https://api.curseforge.com/v1/fingerprints/432", request.RequestUri!.AbsoluteUri); Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("fixture-only", request.Headers.GetValues("x-api-key").Single());
            var file = new { id = 9, modId = 7, gameId = 432, fileFingerprint = 123u, fileName = "fixture.jar", fileLength = 3,
                hashes = new[] { new { algo = 1, value = new string('a', 40) } }, downloadUrl = "https://edge.forgecdn.net/files/1/2/fixture.jar", gameVersions = new[] { "1.21.1", "Fabric" } };
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { data = new { exactMatches = new[] { new { id = mismatch ? 8 : 7, file } } } })));
        }));
        var catalog = new CurseForgeCatalog(http, "fixture-only");
        if (mismatch) { await Assert.ThrowsAsync<InvalidDataException>(() => catalog.MatchFingerprintsAsync([123u], default)); return; }
        var match = (await catalog.MatchFingerprintsAsync([123u], default)).Single(); Assert.AreEqual(123u, match.Key); Assert.AreEqual("cf-7", match.Value.ProjectId); Assert.AreEqual(new string('a', 40), match.Value.Files.Single().Sha1);
    }
    [TestMethod]
    public async Task RestoreRejectsAnExternallyChangedManifestAndRollsFilesAndReceiptBack()
    {
        var i = Instance(); var a = await Mod(i, "one.jar"); var trash = await ModFileManagement.TrashAsync(i, [a.RelativePath], () => { }, default);
        var manifest = Path.Combine(i.GameDirectory, ContentInstallFiles.ManifestName); var calls = 0; var external = "{\"FormatVersion\":1,\"InstanceId\":\"fixture\",\"Files\":[],\"external\":true}";
        await Assert.ThrowsAsync<IOException>(() => ModFileManagement.RestoreAsync(i, trash.Id, () => { if (++calls == 2) File.WriteAllText(manifest, external); }, default));
        Assert.AreEqual(external, await File.ReadAllTextAsync(manifest)); Assert.IsFalse(File.Exists(Path.Combine(i.GameDirectory, a.RelativePath))); Assert.AreEqual(trash.Id, (await ModFileManagement.ListAsync(i, default)).Single().Id);
    }
    [TestMethod]
    public async Task BatchedIdentityAndSixHourCacheAvoidRepeatRequestsButChangedHashIsQueried()
    {
        var bytes = "public mod bytes"u8.ToArray(); var sha = Convert.ToHexStringLower(SHA1.HashData(bytes)); var calls = 0; var batches = 0;
        object Release(bool newer) => new { id = newer ? "next" : "old", project_id = "fixture", name = "Fixture", version_number = newer ? "2" : "1", date_published = newer ? "2026-01-02T00:00:00Z" : "2026-01-01T00:00:00Z", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, version_type = "release", files = new[] { new { filename = "fixture.jar", url = "https://cdn.modrinth.com/data/fixture/versions/old/fixture.jar", hashes = new { sha1 = newer ? new string('a', 40) : sha }, size = bytes.Length, primary = true } }, dependencies = Array.Empty<object>() };
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Interlocked.Increment(ref calls); var path = request.RequestUri!.AbsolutePath; object body;
            if (path.EndsWith("/version_files")) { Interlocked.Increment(ref batches); Assert.AreEqual(HttpMethod.Post, request.Method); body = new Dictionary<string, object> { [sha] = Release(false) }; }
            else if (path.EndsWith("/version")) body = new[] { Release(true) };
            else if (path.EndsWith("/project/fixture")) body = new { id = "fixture", slug = "fixture", title = "Online title", description = "Public", project_type = "mod", downloads = 1, categories = new[] { "optimization", "utility" } };
            else return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(body)));
        }));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); var game = Path.Combine(platform.Paths.InstancesDirectory, "fixture"); Directory.CreateDirectory(Path.Combine(game, "mods")); await File.WriteAllBytesAsync(Path.Combine(game, "mods/fixture.jar"), bytes);
        var instance = new GameInstance("fixture", "Fixture", "1.21.1", game, Loader: "fabric", GameVersionId: "1.21.1");
        await using (var store = new ConfigurationStore(platform.Paths)) await store.SaveAsync(new(new(), [instance], []));
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        var first = await service.CheckInstanceModUpdatesAsync(instance.Id); Assert.HasCount(1, first.Updates); Assert.IsNotNull(first.OnlineInfo); Assert.AreEqual("Online title", first.OnlineInfo.Single().Project.Title); Assert.HasCount(2, first.OnlineInfo.Single().Project.Categories!); var before = calls;
        var second = await service.CheckInstanceModUpdatesAsync(instance.Id); Assert.HasCount(1, second.Updates); Assert.AreEqual(before, calls); Assert.AreNotEqual(first.Updates[0].Token, second.Updates[0].Token);
        var forced = await service.RefreshInstanceModUpdatesAsync(instance.Id); Assert.HasCount(1, forced.Updates); Assert.IsTrue(calls > before); before = calls;
        await File.WriteAllTextAsync(Path.Combine(game, "mods/fixture.jar"), "changed"); var third = await service.CheckInstanceModUpdatesAsync(instance.Id); Assert.HasCount(0, third.Updates); Assert.IsTrue(calls > before); Assert.AreEqual(3, batches);
    }
}
