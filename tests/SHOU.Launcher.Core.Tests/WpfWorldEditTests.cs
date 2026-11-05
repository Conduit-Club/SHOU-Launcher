using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfWorldEditTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task MetadataSaveChangesOnlyThreeAuthorizedBytesAndKeepsExactOriginalBackup(bool gzip)
    {
        var fixture = new Fixture(gzip); var original = await File.ReadAllBytesAsync(fixture.Level);
        var read = await fixture.Store.GetEditStateAsync("world"); Assert.IsTrue(read.CanEditMetadata); Assert.AreEqual(123456L, read.Values.WorldTicks); Assert.AreEqual(-60, read.Values.SpawnY);
        var receipt = await fixture.Store.SaveMetadataAsync("world", read.Revision, new(true, 3, true));
        Assert.IsNotNull(receipt.BackupPath); CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(receipt.BackupPath));
        var after = await File.ReadAllBytesAsync(fixture.Level); CollectionAssert.AreEqual(LevelBytes(allow: 1, difficulty: 3, locked: 1), Plain(after));
        Assert.AreNotEqual(read.Revision, receipt.Revision); var actual = await fixture.Store.GetEditStateAsync("world"); Assert.IsTrue(actual.Values.AllowCommands); Assert.AreEqual(3, actual.Values.Difficulty); Assert.IsTrue(actual.Values.DifficultyLocked);
        Assert.AreEqual(gzip, after[0] == 31 && after[1] == 139); Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Level)!, ".shou-world-*.tmp").Any());
    }
    [TestMethod]
    public async Task StaleLevelRevisionNeverOverwritesExternalChangesOrCreatesBackup()
    {
        var fixture = new Fixture(); var read = await fixture.Store.GetEditStateAsync("world"); var external = LevelBytes(allow: 1);
        await File.WriteAllBytesAsync(fixture.Level, external);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveMetadataAsync("world", read.Revision, new(false, 3, false)));
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod]
    public async Task UnknownVersionAndMissingOrMalformedMetadataCannotBeSaved()
    {
        var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.Level, LevelBytes(dataVersion: 999999)); var unknown = await fixture.Store.GetEditStateAsync("world");
        Assert.IsFalse(unknown.CanEditMetadata); Assert.IsFalse(unknown.CanToggleDatapacks);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SaveMetadataAsync("world", unknown.Revision, new(true, 2, false)));
        await File.WriteAllBytesAsync(fixture.Level, LevelBytes(allow: 2));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.GetEditStateAsync("world"));
        var malformed = Compound("", Compound("Data", Int("DataVersion", 3955), Compound("Version", String("Name", "1.21.1")), Int("allowCommands", 1)));
        await File.WriteAllBytesAsync(fixture.Level, malformed);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.SaveMetadataAsync("world", Hash(malformed), new(true, 2, false)));
        CollectionAssert.AreEqual(malformed, await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod]
    public async Task LockedDifficultyRequiresExplicitUnlockAndHardcoreRemainsHard()
    {
        var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.Level, LevelBytes(locked: 1)); var read = await fixture.Store.GetEditStateAsync("world");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SaveMetadataAsync("world", read.Revision, new(false, 3, true)));
        var saved = await fixture.Store.SaveMetadataAsync("world", read.Revision, new(false, 3, false)); Assert.IsNotNull(saved.BackupPath);
        await File.WriteAllBytesAsync(fixture.Level, LevelBytes(hardcore: 1, difficulty: 3)); read = await fixture.Store.GetEditStateAsync("world");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SaveMetadataAsync("world", read.Revision, new(true, 0, false)));
        CollectionAssert.AreEqual(LevelBytes(hardcore: 1, difficulty: 3), await File.ReadAllBytesAsync(fixture.Level));
    }
    [TestMethod]
    public async Task ReadOnlyAndCancelledMetadataKeepOriginalAndCreateNoBackup()
    {
        var fixture = new Fixture(); var original = await File.ReadAllBytesAsync(fixture.Level); var read = await fixture.Store.GetEditStateAsync("world");
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Store.SaveMetadataAsync("world", read.Revision, new(true, 2, false), new CancellationToken(true)));
        File.SetAttributes(fixture.Level, File.GetAttributes(fixture.Level) | FileAttributes.ReadOnly);
        try { await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveMetadataAsync("world", read.Revision, new(true, 2, false))); }
        finally { File.SetAttributes(fixture.Level, File.GetAttributes(fixture.Level) & ~FileAttributes.ReadOnly); }
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod]
    public async Task EnableDisableZipActuallyMovesOnlySelectedIdAndKeepsUnknownListsAndFields()
    {
        var fixture = new Fixture(true); fixture.Pack("fixture.zip"); var original = await File.ReadAllBytesAsync(fixture.Level);
        var read = await fixture.Store.GetEditStateAsync("world"); var row = read.Datapacks.Single(); Assert.IsFalse(row.Enabled); Assert.IsTrue(row.CanToggle); Assert.AreEqual("Synthetic pack", row.Description);
        var saved = await fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], true);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(saved.BackupPath!));
        CollectionAssert.AreEqual(LevelBytes(active: ["vanilla", "mod:future", "file/fixture.zip"], inactive: ["file/missing.zip"]), Plain(await File.ReadAllBytesAsync(fixture.Level)));
        read = await fixture.Store.GetEditStateAsync("world"); row = read.Datapacks.Single(); Assert.IsTrue(row.Enabled);
        await fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], false);
        CollectionAssert.AreEqual(LevelBytes(active: ["vanilla", "mod:future"], inactive: ["file/missing.zip", "file/fixture.zip"]), Plain(await File.ReadAllBytesAsync(fixture.Level)));
    }
    [TestMethod]
    public async Task ChangedPackContentsOrLevelRevisionRejectsSelectionWithoutMutation()
    {
        var fixture = new Fixture(); var path = fixture.Pack("fixture.zip"); var read = await fixture.Store.GetEditStateAsync("world"); var selected = read.Datapacks.Single();
        await File.WriteAllBytesAsync(path, Zip("{\"pack\":{\"pack_format\":48,\"description\":\"Changed after selection\"}}"));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(selected.PackId, selected.ContentRevision)], true));
        var external = LevelBytes(allow: 1); await File.WriteAllBytesAsync(fixture.Level, external);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(selected.PackId, selected.ContentRevision)], true));
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod]
    public async Task BuiltinFeatureAndUnverifiedFormatPacksCannotBeToggled()
    {
        var fixture = new Fixture(); fixture.Pack("feature.zip", "{\"features\":{\"enabled\":[\"minecraft:bundle\"]},\"pack\":{\"pack_format\":48,\"description\":\"feature\"}}");
        fixture.Pack("old.zip", "{\"pack\":{\"pack_format\":15,\"description\":\"old format\"}}");
        var read = await fixture.Store.GetEditStateAsync("world"); Assert.IsTrue(read.Datapacks.All(p => !p.CanToggle));
        foreach (var row in read.Datapacks) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], true));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new("vanilla", "irrelevant")], false));
        CollectionAssert.AreEqual(LevelBytes(), await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod]
    public async Task CorruptZipTraversalAndExpansionBombAreNotPresentedAsToggleable()
    {
        var fixture = new Fixture(); fixture.Pack("bad.zip", "{broken"); var bad = Path.Combine(fixture.Packs, "traversal.zip"); await File.WriteAllBytesAsync(bad, Zip("{\"pack\":{\"pack_format\":48}}", "../outside", "malicious"));
        var bomb = Path.Combine(fixture.Packs, "bomb.zip"); await File.WriteAllBytesAsync(bomb, Zip("{\"pack\":{\"pack_format\":48}}", "data/bomb.txt", new string('x', 1024 * 1024)));
        var read = await fixture.Store.GetEditStateAsync("world"); Assert.AreEqual(3, read.Datapacks.Count); Assert.IsTrue(read.Datapacks.All(p => !p.CanToggle));
        foreach (var row in read.Datapacks) await Assert.ThrowsAsync<Exception>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], true));
        CollectionAssert.AreEqual(LevelBytes(), await File.ReadAllBytesAsync(fixture.Level)); Assert.IsEmpty(fixture.Backups()); Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "outside")));
    }
    [TestMethod]
    public async Task FolderPackRoundTripsAndNestedLinkCannotEscapeOrBeEnabled()
    {
        var fixture = new Fixture(); var pack = Path.Combine(fixture.Packs, "folder"); Directory.CreateDirectory(pack); await File.WriteAllTextAsync(Path.Combine(pack, "pack.mcmeta"), "{\"pack\":{\"pack_format\":48,\"description\":{\"text\":\"Folder\",\"extra\":[{\"text\":\" pack\"}]}}}");
        var read = await fixture.Store.GetEditStateAsync("world"); var row = read.Datapacks.Single(); Assert.IsTrue(row.CanToggle); Assert.IsNull(row.Enabled); Assert.AreEqual("Folder pack", row.Description);
        await fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], true);
        Assert.IsTrue((await fixture.Store.GetEditStateAsync("world")).Datapacks.Single().Enabled);
        var external = Path.Combine(fixture.Root, "external"); Directory.CreateDirectory(external); await File.WriteAllTextAsync(Path.Combine(external, "untouched.txt"), "protected"); Directory.CreateSymbolicLink(Path.Combine(pack, "data"), external);
        read = await fixture.Store.GetEditStateAsync("world"); Assert.IsFalse(read.Datapacks.Single().CanToggle);
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.SetDatapacksEnabledAsync("world", read.Revision, [new(row.PackId, row.ContentRevision)], false));
        Assert.AreEqual("protected", await File.ReadAllTextAsync(Path.Combine(external, "untouched.txt")));
    }
    [TestMethod]
    public async Task AmbiguousDatapackListsAndLevelLinksNeverOverwriteOriginal()
    {
        var fixture = new Fixture(); var ambiguous = LevelBytes(active: ["vanilla", "file/fixture.zip"]); await File.WriteAllBytesAsync(fixture.Level, ambiguous);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.GetEditStateAsync("world"));
        CollectionAssert.AreEqual(ambiguous, await File.ReadAllBytesAsync(fixture.Level));
        var external = Path.Combine(fixture.Root, "outside-level.dat"); await File.WriteAllBytesAsync(external, LevelBytes()); File.Delete(fixture.Level); File.CreateSymbolicLink(fixture.Level, external);
        await Assert.ThrowsAsync<IOException>(() => fixture.Store.SaveMetadataAsync("world", Hash(LevelBytes()), new(true, 3, false)));
        CollectionAssert.AreEqual(LevelBytes(), await File.ReadAllBytesAsync(external)); Assert.IsEmpty(fixture.Backups());
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task NewServiceEntryPointsRequireSelectedIsolatedTargetWithoutCredentials(bool isolated)
    {
        var downloader = new NoDownloads(); var native = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); var credentials = new NoCredentials();
        var platform = native with { Credentials = credentials, Paths = new FixturePaths(native.Paths, isolated) };
        using var lease = platform.DataLock.Acquire(platform.Paths); using var http = new HttpClient(new NoNetwork());
        await using var processes = (ProcessRunner)native.Processes; await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        var first = await service.CreateInstanceAsync("First edit fixture", "1.21.1"); var second = await service.CreateInstanceAsync("Second edit fixture", "1.21.1");
        foreach (var instance in new[] { first, second }) { var path = Path.Combine(instance.GameDirectory, "saves/world"); Directory.CreateDirectory(path); await File.WriteAllBytesAsync(Path.Combine(path, "level.dat"), LevelBytes()); }
        var edits = (IInstanceWorldEditService)service;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => edits.GetWorldEditStateAsync(first.Id, "world"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => edits.SaveWorldMetadataAsync(first.Id, "world", Hash(LevelBytes()), new(true, 3, false)));
        if (!isolated) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => edits.SaveWorldMetadataAsync(second.Id, "world", Hash(LevelBytes()), new(true, 3, false)));
        else
        {
            var read = await edits.GetWorldEditStateAsync(second.Id, "world"); var saved = await edits.SaveWorldMetadataAsync(second.Id, "world", read.Revision, new(true, 3, false));
            Assert.IsNotNull(saved.BackupPath); CollectionAssert.AreEqual(LevelBytes(allow: 1, difficulty: 3), await File.ReadAllBytesAsync(Path.Combine(second.GameDirectory, "saves/world/level.dat")));
        }
        Assert.AreEqual(0, credentials.Calls); CollectionAssert.AreEqual(LevelBytes(), await File.ReadAllBytesAsync(Path.Combine(first.GameDirectory, "saves/world/level.dat")));
    }
    private sealed class Fixture
    {
        public string Root { get; } = TestFiles.Root();
        public string Game { get; }
        public string Level => Path.Combine(Game, "saves/world/level.dat");
        public string Packs => Path.Combine(Game, "saves/world/datapacks");
        public InstanceWorldStore Store { get; }
        public Fixture(bool gzip = false) { Game = Path.Combine(Root, "instance"); Directory.CreateDirectory(Packs); var bytes = LevelBytes(); File.WriteAllBytes(Level, gzip ? Gzip(bytes) : bytes); Store = new(Root, Game); }
        public string Pack(string name, string metadata = "{\"pack\":{\"pack_format\":48,\"description\":\"Synthetic pack\"}}") { var path = Path.Combine(Packs, name); File.WriteAllBytes(path, Zip(metadata)); return path; }
        public string[] Backups() => Directory.GetFiles(Path.GetDirectoryName(Level)!, "*.shou-backup-*");
    }
    private sealed class NoCredentials : ICredentialStore
    {
        public int Calls;
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Calls++; throw new AssertFailedException("World file actions must not read credentials."); }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) { Calls++; throw new AssertFailedException("World file actions must not write credentials."); }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Calls++; throw new AssertFailedException("World file actions must not delete credentials."); }
    }
    private sealed class FixturePaths(IAppPaths source, bool isolated) : IAppPaths
    {
        public string DataDirectory => source.DataDirectory;
        public string SharedDataDirectory => source.SharedDataDirectory;
        public string LocalDataDirectory => source.LocalDataDirectory;
        public string CacheDirectory => source.CacheDirectory;
        public string LogsDirectory => source.LogsDirectory;
        public string RuntimesDirectory => source.RuntimesDirectory;
        public string InstancesDirectory => source.InstancesDirectory;
        public string TempDirectory => source.TempDirectory;
        public string ResourcesDirectory => source.ResourcesDirectory;
        public string LockFile => source.LockFile;
        public string CredentialScope => source.CredentialScope;
        public bool IsIsolated => isolated;
    }
    private sealed class NoDownloads : IVerifiedDownloader
    { public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("World fixtures must not download."); }
    private sealed class NoNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new AssertFailedException("World fixtures must not use the network."); }

    // Independent fixed-tag fixture encoder. Byte-for-byte output comparisons above do not use the production NBT reader.
    private static byte[] LevelBytes(byte allow = 0, byte difficulty = 2, byte locked = 0, byte hardcore = 0, int dataVersion = 3955, string[]? active = null, string[]? inactive = null) => Compound("",
        String("FutureRoot", "preserve-root"), Compound("Data", String("LevelName", "Synthetic world"), Int("DataVersion", dataVersion), Compound("Version", String("Name", "1.21.1")),
        Byte("allowCommands", allow), Byte("Difficulty", difficulty), Byte("DifficultyLocked", locked), Byte("hardcore", hardcore), Int("SpawnX", 16), Int("SpawnY", -60), Int("SpawnZ", -32), Long("Time", 123456), Long("RandomSeed", -9223372036854775000),
        Compound("DataPacks", List("Enabled", active ?? ["vanilla", "mod:future"]), List("Disabled", inactive ?? ["file/fixture.zip", "file/missing.zip"]), Long("FuturePackField", 987654321)),
        List("enabled_features", ["minecraft:vanilla"]), String("FutureData", "preserve-data")));
    private static byte[] Compound(string name, params byte[][] children) => Tag(10, name, children.SelectMany(c => c).Append((byte)0).ToArray());
    private static byte[] String(string name, string value) => Tag(8, name, Utf(value));
    private static byte[] Byte(string name, byte value) => Tag(1, name, [value]);
    private static byte[] Int(string name, int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return Tag(3, name, bytes); }
    private static byte[] Long(string name, long value) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return Tag(4, name, bytes); }
    private static byte[] List(string name, string[] values) { var bytes = new byte[5]; bytes[0] = 8; BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), values.Length); return Tag(9, name, bytes.Concat(values.SelectMany(Utf)).ToArray()); }
    private static byte[] Tag(byte type, string name, byte[] payload) => new[] { type }.Concat(Utf(name)).Concat(payload).ToArray();
    private static byte[] Utf(string value) { var bytes = Encoding.UTF8.GetBytes(value); var size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length); return size.Concat(bytes).ToArray(); }
    private static byte[] Gzip(byte[] value) { using var result = new MemoryStream(); using (var gzip = new GZipStream(result, CompressionLevel.Optimal, true)) gzip.Write(value); return result.ToArray(); }
    private static byte[] Plain(byte[] value) { if (value[0] != 31) return value; using var input = new MemoryStream(value); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray(); }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Zip(string metadata, string? name = null, string content = "")
    { using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) { using (var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open(), new UTF8Encoding(false))) writer.Write(metadata); if (name is not null) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); } } return output.ToArray(); }
}
