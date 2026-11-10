using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class WorldContentInstallTests
{
    private static async Task<WorldDatapackUpdateReceipt> CreateRestorableUpdate(Fixture f, bool dependency = false, bool large = false)
    {
        await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        var json = JsonNode.Parse(File.ReadAllBytes(PackManifest(f)))!; json["Future"] = "root-kept"; json["Files"]![0]!["FutureFile"] = "file-kept"; await File.WriteAllTextAsync(PackManifest(f), json.ToJsonString());
        var bytes = f.Pack("main", "new");
        if (large) { var random = new byte[16 * 1024 * 1024]; RandomNumberGenerator.Fill(random); bytes = ZipBytes(new() { ["pack.mcmeta"] = Encoding.UTF8.GetBytes("{\"pack\":{\"pack_format\":48}}"), ["data/test/function/main.mcfunction"] = random }); }
        if (dependency) f.Catalog.Add("dependency", f.Pack("dependency", "api"));
        f.Catalog.AddVersion("main", "v2", bytes, dependency ? [new("required", "dependency", "dependency")] : null);
        return await f.Service.ApplyWorldDatapackUpdateAsync((await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", "v2"))).Id);
    }
    private static string BackupId(WorldDatapackUpdateReceipt receipt) => Path.GetFileName(receipt.BackupDirectory);
    [TestMethod, DataRow(false, false), DataRow(true, false), DataRow(false, true)]
    public async Task UpdateBackupRestoreReinstatesExactFilesMetadataAndOriginAndPreservesBothVersions(bool modern, bool dependency)
    {
        await using var f = await Fixture.Create(modern); var update = await CreateRestorableUpdate(f, dependency); var id = BackupId(update);
        var newPack = File.ReadAllBytes(Path.Combine(Packs(f), "v2.zip")); var newLevel = File.ReadAllBytes(f.Level); var newManifest = File.ReadAllBytes(PackManifest(f));
        var listing = await f.Service.GetWorldDatapackBackupsAsync(f.Instance.Id, "现有世界"); Assert.IsTrue(listing.Single().CanRestore);
        var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", id); CollectionAssert.AreEqual(newPack, File.ReadAllBytes(Path.Combine(Packs(f), "v2.zip")));
        var receipt = await f.Service.RestoreWorldDatapackBackupAsync(preview.Id);
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(update.BackupDirectory, "payload/main.zip")), File.ReadAllBytes(Path.Combine(Packs(f), "main.zip")));
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(update.BackupDirectory, "level.before")), File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(update.BackupDirectory, "manifest.before")), File.ReadAllBytes(PackManifest(f)));
        CollectionAssert.AreEqual(newPack, File.ReadAllBytes(Path.Combine(receipt.PreservedCurrentDirectory, "payload/v2.zip"))); CollectionAssert.AreEqual(newLevel, File.ReadAllBytes(Path.Combine(receipt.PreservedCurrentDirectory, "level.before"))); CollectionAssert.AreEqual(newManifest, File.ReadAllBytes(Path.Combine(receipt.PreservedCurrentDirectory, "manifest.before")));
        Assert.IsFalse(File.Exists(Path.Combine(Packs(f), "v2.zip"))); if (dependency) { Assert.IsFalse(File.Exists(Path.Combine(Packs(f), "dependency.zip"))); Assert.IsTrue(File.Exists(Path.Combine(receipt.PreservedCurrentDirectory, "payload/dependency.zip"))); }
        Assert.IsFalse(Directory.GetDirectories(f.World, ".shou-datapack-restore-*").Any());
        await f.RestartServiceAsync(); var reopened = (await f.Service.GetWorldDatapackBackupsAsync(f.Instance.Id, "现有世界")).Single(); Assert.IsFalse(reopened.CanRestore); StringAssert.Contains(reopened.Status, "已恢复");
        Assert.AreEqual("main", (await f.Service.GetWorldDatapackUpdateOptionsAsync(f.Instance.Id, "现有世界", "file/main.zip")).CurrentReleaseId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreWorldDatapackBackupAsync(preview.Id));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task ManualZipReplacementRestoresOriginalFileOrFolderAndDisabledOrder(bool folder)
    {
        await using var f = await Fixture.Create(); Directory.CreateDirectory(Packs(f)); var name = folder ? "local" : "local.zip"; var original = f.Pack("local", "old");
        if (folder) { var zip = await LocalArchiveAsync(original); ZipFile.ExtractToDirectory(zip, Path.Combine(Packs(f), name)); Directory.CreateDirectory(Path.Combine(Packs(f), name, "empty")); }
        else await File.WriteAllBytesAsync(Path.Combine(Packs(f), name), original);
        await PackListsAsync(f, ["vanilla"], ["file/" + name]); var originalLevel = File.ReadAllBytes(f.Level); var originalFiles = ActivePayloadFiles(f);
        var local = await LocalArchiveAsync(f.Pack("local", "new")); var update = await f.Service.ApplyWorldDatapackUpdateAsync((await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, name, local: local))).Id);
        var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", BackupId(update)); await f.Service.RestoreWorldDatapackBackupAsync(preview.Id);
        CollectionAssert.AreEqual(originalFiles, ActivePayloadFiles(f)); CollectionAssert.AreEqual(originalLevel, File.ReadAllBytes(f.Level)); Assert.IsFalse(File.Exists(PackManifest(f))); if (folder) Assert.IsTrue(Directory.Exists(Path.Combine(Packs(f), name, "empty")));
    }
    [TestMethod, DataRow("payload"), DataRow("levelBackup"), DataRow("receipt"), DataRow("missingSnapshot"), DataRow("levelChanged"), DataRow("packChanged"), DataRow("target"), DataRow("readonly"), DataRow("cancel")]
    public async Task RestoreFailureOrCancellationKeepsUpdatedFilesAndRetainsOriginalBackup(string scenario)
    {
        await using var f = await Fixture.Create(); var update = await CreateRestorableUpdate(f); var id = BackupId(update);
        var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", id);
        if (scenario == "payload") await File.WriteAllBytesAsync(Path.Combine(update.BackupDirectory, "payload/main.zip"), f.Pack("main", "tampered"));
        if (scenario == "levelBackup") await File.AppendAllTextAsync(Path.Combine(update.BackupDirectory, "level.before"), "changed");
        if (scenario == "receipt") { var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(update.BackupDirectory, "receipt.json")))!; json["Files"]![0]!["OldName"] = "../outside"; await File.WriteAllTextAsync(Path.Combine(update.BackupDirectory, "receipt.json"), json.ToJsonString()); }
        if (scenario == "missingSnapshot") File.Delete(Path.Combine(update.BackupDirectory, "level.after"));
        if (scenario == "levelChanged") await PackListsAsync(f, ["vanilla"], ["file/v2.zip"]);
        if (scenario == "packChanged") await File.WriteAllBytesAsync(Path.Combine(Packs(f), "v2.zip"), f.Pack("main", "external"));
        if (scenario == "target") await File.WriteAllTextAsync(Path.Combine(Packs(f), "main.zip"), "foreign");
        var files = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f)); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "readonly") File.SetAttributes(PackManifest(f), FileAttributes.ReadOnly);
        try { await Reject(() => f.Service.RestoreWorldDatapackBackupAsync(preview.Id, cts.Token)); }
        finally { if (scenario == "readonly") File.SetAttributes(PackManifest(f), FileAttributes.Normal); }
        CollectionAssert.AreEqual(files, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f)));
        Assert.IsTrue(File.Exists(Path.Combine(update.BackupDirectory, "payload/main.zip"))); Assert.IsFalse(File.Exists(Path.Combine(update.BackupDirectory, "restored"))); Assert.IsFalse(Directory.GetDirectories(f.World, ".shou-datapack-restore-*").Any());
    }
    [TestMethod]
    public async Task RestoreCancelledAfterMovingCurrentPackRollsBackWithoutConsumingOriginalBackup()
    {
        await using var f = await Fixture.Create(); var update = await CreateRestorableUpdate(f, large: true); var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", BackupId(update));
        var files = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f)); using var cts = new CancellationTokenSource(); var reached = new TaskCompletionSource();
        using var watcher = new FileSystemWatcher(update.BackupDirectory, "v2.zip") { IncludeSubdirectories = true };
        watcher.Created += (_, e) => { if (e.FullPath.Contains("restore-current", StringComparison.Ordinal)) { reached.TrySetResult(); cts.Cancel(); } }; watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.RestoreWorldDatapackBackupAsync(preview.Id, cts.Token)); await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(files, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f)));
        Assert.IsTrue(File.Exists(Path.Combine(update.BackupDirectory, "payload/main.zip"))); Assert.IsFalse(File.Exists(Path.Combine(update.BackupDirectory, "restored"))); Assert.IsFalse(Directory.GetDirectories(f.World, ".shou-datapack-restore-*").Any());
    }
    [TestMethod]
    public async Task LegacyAndCorruptBackupRecordsStayVisibleReadOnlyAndDoNotHideValidBackup()
    {
        await using var f = await Fixture.Create(); var update = await CreateRestorableUpdate(f); var root = Path.GetDirectoryName(update.BackupDirectory)!;
        foreach (var text in new[] { "{\"Format\":1}", "invalid JSON" }) { var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), text); }
        var entries = await f.Service.GetWorldDatapackBackupsAsync(f.Instance.Id, "现有世界"); Assert.HasCount(3, entries); Assert.HasCount(1, entries.Where(e => e.CanRestore)); Assert.IsTrue(entries.Any(e => e.Status.Contains("旧版")));
        Assert.AreEqual(update.BackupDirectory, await f.Service.GetWorldDatapackBackupDirectoryAsync(f.Instance.Id, "现有世界", BackupId(update)));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.GetWorldDatapackBackupDirectoryAsync(f.Instance.Id, "现有世界", "../outside"));
    }
    [TestMethod]
    public async Task MultipleUpdatesCanRestoreInReverseOrderAndRefuseToOverwriteLaterEdits()
    {
        await using var f = await Fixture.Create(); var first = await CreateRestorableUpdate(f); f.Catalog.AddVersion("main", "v3", f.Pack("main", "third"));
        var second = await f.Service.ApplyWorldDatapackUpdateAsync((await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "v2.zip", "v3"))).Id);
        var entries = await f.Service.GetWorldDatapackBackupsAsync(f.Instance.Id, "现有世界"); Assert.IsFalse(entries.Single(e => e.Id == BackupId(first)).CanRestore);
        var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", BackupId(second)); await f.Service.RestoreWorldDatapackBackupAsync(preview.Id);
        Assert.IsTrue((await f.Service.GetWorldDatapackBackupsAsync(f.Instance.Id, "现有世界")).Single(e => e.Id == BackupId(first)).CanRestore);
        preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", BackupId(first)); await f.Service.RestoreWorldDatapackBackupAsync(preview.Id); Assert.IsTrue(File.Exists(Path.Combine(Packs(f), "main.zip")));
    }
    [TestMethod]
    public async Task RestoreRejectsChangedInstanceSelectionAndExplicitlyDiscardedPreview()
    {
        await using var f = await Fixture.Create(); var update = await CreateRestorableUpdate(f); var preview = await f.Service.PreviewWorldDatapackRestoreAsync(f.Instance.Id, "现有世界", BackupId(update));
        var files = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); await f.Service.CreateInstanceAsync("其它目标", "1.21.1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreWorldDatapackBackupAsync(preview.Id));
        await f.Service.SelectInstanceAsync(f.Instance.Id); await f.Service.DiscardWorldDatapackRestoreAsync(preview.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RestoreWorldDatapackBackupAsync(preview.Id));
        CollectionAssert.AreEqual(files, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level));
    }
}
