using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WorldDatapackTrashTests
{
    [TestMethod]
    public async Task ZipAndFolderRecycleRestoreAfterServiceRestartWithExactBackupsAndUnknownFields()
    {
        var f = new Fixture(); var before = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(f.Manifest);
        var zip = File.ReadAllBytes(Path.Combine(f.Packs, "fixture.zip")); var folder = File.ReadAllBytes(Path.Combine(f.Packs, "folder/data/test/function/a.mcfunction"));
        await f.RecycleAsync();
        Assert.IsFalse(File.Exists(Path.Combine(f.Packs, "fixture.zip"))); Assert.IsFalse(Directory.Exists(Path.Combine(f.Packs, "folder")));
        var post = await f.Store.GetEditStateAsync("世界 one"); Assert.HasCount(0, post.Datapacks);
        var data = BoundedNbt.Read(File.ReadAllBytes(f.Level)).Root.Find("Data")!;
        CollectionAssert.AreEqual(new[] { "vanilla", "mod:unknown" }, ((NbtList)data.Find("DataPacks")!.Find("Enabled")!.Value).Items.Select(i => (string)i.Value).ToArray());
        Assert.AreEqual("keep-data", data.Find("Future")!.Value);
        var currentManifest = JsonNode.Parse(File.ReadAllBytes(f.Manifest))!;
        Assert.AreEqual(7, (int)currentManifest["Future"]!["nested"]!); Assert.AreEqual(1, currentManifest["Files"]!.AsArray().Count);
        Assert.IsTrue(Directory.GetFiles(f.World, "level.dat.shou-backup-*").Any(p => File.ReadAllBytes(p).SequenceEqual(before)));
        Assert.IsTrue(Directory.GetFiles(f.World, ".shou-content-manifest.json.shou-backup-*").Any(p => File.ReadAllBytes(p).SequenceEqual(manifest)));
        var restarted = new InstanceWorldStore(f.Root, f.Game); var receipt = (await restarted.GetDatapackTrashAsync("世界 one")).Single(); Assert.IsTrue(receipt.CanRestore);
        await restarted.RestoreDatapacksAsync("世界 one", receipt.Id);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(f.Manifest));
        CollectionAssert.AreEqual(zip, File.ReadAllBytes(Path.Combine(f.Packs, "fixture.zip"))); CollectionAssert.AreEqual(folder, File.ReadAllBytes(Path.Combine(f.Packs, "folder/data/test/function/a.mcfunction")));
        Assert.HasCount(0, await new InstanceWorldStore(f.Root, f.Game).GetDatapackTrashAsync("世界 one"));
        Assert.HasCount(0, Directory.GetFiles(f.World, "*.tmp", SearchOption.AllDirectories));
    }
    [TestMethod]
    public async Task StaleLevelOrPackAndUnsupportedVersionNeverMoveFiles()
    {
        var f = new Fixture(); var state = await f.Store.GetEditStateAsync("世界 one");
        using (var changed = ZipFile.Open(Path.Combine(f.Packs, "fixture.zip"), ZipArchiveMode.Update))
        { changed.GetEntry("pack.mcmeta")!.Delete(); using var writer = new StreamWriter(changed.CreateEntry("pack.mcmeta").Open()); writer.Write("{\"pack\":{\"pack_format\":48,\"description\":\"changed valid ZIP\"}}"); }
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Store.RecycleDatapacksAsync("世界 one", state.Revision, [Select(state.Datapacks.First(p => p.File.Name == "fixture.zip"))]));
        Assert.IsTrue(File.Exists(Path.Combine(f.Packs, "fixture.zip")));
        var other = new Fixture(); var original = File.ReadAllBytes(other.Level); state = await other.Store.GetEditStateAsync("世界 one");
        await other.Store.SaveMetadataAsync("世界 one", state.Revision, new(true, 2, false));
        await Assert.ThrowsExactlyAsync<IOException>(() => other.Store.RecycleDatapacksAsync("世界 one", state.Revision, state.Datapacks.Select(Select).ToArray()));
        var unsupported = new Fixture(4000); var unknown = await unsupported.Store.GetEditStateAsync("世界 one");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unsupported.Store.RecycleDatapacksAsync("世界 one", unknown.Revision, unknown.Datapacks.Select(Select).ToArray()));
        Assert.IsTrue(Directory.Exists(Path.Combine(unsupported.Packs, "folder")));
    }
    [TestMethod]
    public async Task ManifestWriteFailureRollsBackMovedFilesAndLevel()
    {
        var f = new Fixture(); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(f.Manifest);
        File.SetAttributes(f.Manifest, File.GetAttributes(f.Manifest) | FileAttributes.ReadOnly);
        try { await Assert.ThrowsExactlyAsync<IOException>(() => f.RecycleAsync()); }
        finally { File.SetAttributes(f.Manifest, FileAttributes.Normal); }
        CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(f.Manifest));
        Assert.IsTrue(File.Exists(Path.Combine(f.Packs, "fixture.zip"))); Assert.IsTrue(Directory.Exists(Path.Combine(f.Packs, "folder")));
        Assert.HasCount(0, await f.Store.GetDatapackTrashAsync("世界 one")); Assert.HasCount(0, Directory.GetFiles(f.World, "*.tmp", SearchOption.AllDirectories));
    }
    [TestMethod]
    public async Task RestoreWriteFailureKeepsCompleteRecycledStateAndCanRetry()
    {
        var f = new Fixture(); await f.RecycleAsync(); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(f.Manifest);
        var receipt = (await f.Store.GetDatapackTrashAsync("世界 one")).Single();
        File.SetAttributes(f.Manifest, File.GetAttributes(f.Manifest) | FileAttributes.ReadOnly);
        try { await Assert.ThrowsExactlyAsync<IOException>(() => f.Store.RestoreDatapacksAsync("世界 one", receipt.Id)); }
        finally { File.SetAttributes(f.Manifest, FileAttributes.Normal); }
        CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(f.Manifest));
        Assert.IsFalse(File.Exists(Path.Combine(f.Packs, "fixture.zip"))); Assert.IsTrue((await f.Store.GetDatapackTrashAsync("世界 one")).Single().CanRestore);
        await new InstanceWorldStore(f.Root, f.Game).RestoreDatapacksAsync("世界 one", receipt.Id); Assert.IsTrue(File.Exists(Path.Combine(f.Packs, "fixture.zip")));
    }
    [TestMethod]
    public async Task NewWorldChangesAndOccupiedTargetsRejectRestoreWithoutOverwriting()
    {
        var f = new Fixture(); await f.RecycleAsync(); var entry = (await f.Store.GetDatapackTrashAsync("世界 one")).Single(); var before = File.ReadAllBytes(f.Level);
        await File.WriteAllTextAsync(Path.Combine(f.Packs, "fixture.zip"), "new user file");
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Store.RestoreDatapacksAsync("世界 one", entry.Id));
        Assert.AreEqual("new user file", await File.ReadAllTextAsync(Path.Combine(f.Packs, "fixture.zip"))); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level));
        var state = await f.Store.GetEditStateAsync("世界 one"); await f.Store.SaveMetadataAsync("世界 one", state.Revision, new(true, 2, false));
        var modified = File.ReadAllBytes(f.Level); Assert.IsFalse((await f.Store.GetDatapackTrashAsync("世界 one")).Single().CanRestore);
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Store.RestoreDatapacksAsync("世界 one", entry.Id)); CollectionAssert.AreEqual(modified, File.ReadAllBytes(f.Level));
    }
    [TestMethod]
    public async Task RecycledFolderTamperingAndInvalidIdAreRejected()
    {
        var f = new Fixture(); await f.RecycleAsync(); var entry = (await f.Store.GetDatapackTrashAsync("世界 one")).Single();
        await File.AppendAllTextAsync(Path.Combine(f.World, ".shou-trash/datapacks", entry.Id, "payload/folder/data/test/function/a.mcfunction"), "tampered");
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Store.RestoreDatapacksAsync("世界 one", entry.Id)); Assert.IsFalse(Directory.Exists(Path.Combine(f.Packs, "folder")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Store.RestoreDatapacksAsync("世界 one", "../../outside"));
    }
    [TestMethod]
    public async Task CancellationBeforeRecycleOrRestoreDoesNotChangeDisk()
    {
        var f = new Fixture(); var before = File.ReadAllBytes(f.Level); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.RecycleAsync(cts.Token)); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level));
        await f.RecycleAsync(); var entry = (await f.Store.GetDatapackTrashAsync("世界 one")).Single(); before = File.ReadAllBytes(f.Level);
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Store.RestoreDatapacksAsync("世界 one", entry.Id, cts.Token)); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level));
        Assert.IsTrue((await f.Store.GetDatapackTrashAsync("世界 one")).Single().CanRestore);
    }
    [TestMethod]
    public async Task CancellationAfterPayloadMoveRollsBackFilesAndBothMetadataFiles()
    {
        var f = new Fixture(); var path = Path.Combine(f.Packs, "fixture.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using var stream = zip.CreateEntry("data/test/blob/payload.bin", CompressionLevel.NoCompression).Open();
            var block = new byte[65536]; System.Security.Cryptography.RandomNumberGenerator.Fill(block);
            for (var i = 0; i < 512; i++) stream.Write(block);
        }
        var state = await f.Store.GetEditStateAsync("世界 one"); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(f.Manifest);
        using var cts = new CancellationTokenSource(); var observed = new TaskCompletionSource();
        using var watcher = new FileSystemWatcher(f.World, "fixture.zip") { IncludeSubdirectories = true };
        watcher.Created += (_, e) => { if (e.FullPath.Contains(Path.DirectorySeparatorChar + "payload" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { observed.TrySetResult(); cts.Cancel(); } };
        watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Store.RecycleDatapacksAsync("世界 one", state.Revision, state.Datapacks.Select(Select).ToArray(), cts.Token));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(f.Manifest));
        Assert.IsTrue(File.Exists(path)); Assert.IsTrue(Directory.Exists(Path.Combine(f.Packs, "folder"))); Assert.HasCount(0, await f.Store.GetDatapackTrashAsync("世界 one"));
    }
    [TestMethod]
    public async Task ReceiptBudgetRefusesNewRecycleBeforeMovingAnyFiles()
    {
        var f = new Fixture(); var before = File.ReadAllBytes(f.Level);
        for (var i = 0; i < 256; i++) Directory.CreateDirectory(Path.Combine(f.World, ".shou-trash/datapacks", Guid.NewGuid().ToString("N")));
        await Assert.ThrowsExactlyAsync<IOException>(() => f.RecycleAsync());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level)); Assert.IsTrue(File.Exists(Path.Combine(f.Packs, "fixture.zip")));
        Assert.AreEqual(256, Directory.GetDirectories(Path.Combine(f.World, ".shou-trash/datapacks")).Length);
    }
    [TestMethod]
    public async Task DamagedInstallManifestBlocksBeforeAnyPackMove()
    {
        var f = new Fixture(); await File.WriteAllTextAsync(f.Manifest, "{\"FormatVersion\":9,\"Files\":[]}"); var before = File.ReadAllBytes(f.Level);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.RecycleAsync()); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Level));
        Assert.IsTrue(File.Exists(Path.Combine(f.Packs, "fixture.zip"))); Assert.IsFalse(Directory.Exists(Path.Combine(f.World, ".shou-trash")));
    }
    private static WorldDatapackSelection Select(WorldDatapackState state) => new(state.PackId, state.ContentRevision);
    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r22-trash", Guid.NewGuid().ToString("N"));
        public string Game => Path.Combine(Root, "实例 中文");
        public string World => Path.Combine(Game, "saves/世界 one");
        public string Level => Path.Combine(World, "level.dat");
        public string Manifest => Path.Combine(World, ".shou-content-manifest.json");
        public string Packs => Path.Combine(World, "datapacks");
        public InstanceWorldStore Store { get; }
        public Fixture(int version = 3955)
        {
            Directory.CreateDirectory(Path.Combine(Packs, "folder/data/test/function"));
            File.WriteAllText(Path.Combine(Packs, "folder/pack.mcmeta"), "{\"pack\":{\"pack_format\":48,\"description\":\"controlled folder\"}}");
            File.WriteAllText(Path.Combine(Packs, "folder/data/test/function/a.mcfunction"), "say test");
            using (var zip = ZipFile.Open(Path.Combine(Packs, "fixture.zip"), ZipArchiveMode.Create))
            { using var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open()); writer.Write("{\"pack\":{\"pack_format\":48,\"description\":\"controlled ZIP\"}}"); }
            NbtTag List(string name, params string[] values) => new(NbtType.List, name, new NbtList(NbtType.String, values.Select(v => new NbtTag(NbtType.String, "", v)).ToArray()));
            var data = new NbtTag(NbtType.Compound, "Data", new NbtTag[] {
                new(NbtType.String, "LevelName", "Controlled world"), new(NbtType.Int, "DataVersion", version), new(NbtType.Compound, "Version", new NbtTag[] { new(NbtType.String, "Name", "1.21.1") }),
                new(NbtType.Byte, "allowCommands", (sbyte)0), new(NbtType.Byte, "Difficulty", (sbyte)2), new(NbtType.Byte, "DifficultyLocked", (sbyte)0), new(NbtType.String, "Future", "keep-data"),
                new(NbtType.Compound, "DataPacks", new NbtTag[] { List("Enabled", "vanilla", "file/folder", "mod:unknown"), List("Disabled", "file/fixture.zip"), new(NbtType.Long, "Future", 777L) }) });
            File.WriteAllBytes(Level, BoundedNbt.Write(new(new(NbtType.Compound, "", new[] { data }), NbtCompression.Gzip)));
            File.WriteAllText(Manifest, "{\"FormatVersion\":1,\"InstanceId\":\"test\",\"Future\":{\"nested\":7},\"Files\":[{\"RelativePath\":\"datapacks/fixture.zip\",\"FutureFile\":true},{\"RelativePath\":\"datapacks/folder/data/test/function/a.mcfunction\"},{\"RelativePath\":\"other/untouched.zip\"}]}");
            Store = new(Root, Game);
        }
        public async Task RecycleAsync(CancellationToken ct = default) { var state = await Store.GetEditStateAsync("世界 one", ct); await Store.RecycleDatapacksAsync("世界 one", state.Revision, state.Datapacks.Select(Select).ToArray(), ct); }
    }
}
