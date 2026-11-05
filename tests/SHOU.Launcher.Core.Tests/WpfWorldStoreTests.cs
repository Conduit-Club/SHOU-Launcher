using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfWorldStoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ServerEditAddRemoveRoundTripsUnknownRootAndPerRowFieldsWithExactBackup(bool gzip)
    {
        var fixture = new Fixture(); var original = Servers(); if (gzip) original = WpfWorldNbtTests.Gzip(original);
        await File.WriteAllBytesAsync(fixture.ServersPath, original);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.ServersPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var initial = await fixture.Store.GetServersAsync();
        Assert.HasCount(2, initial.Entries); Assert.AreEqual("First", initial.Entries[0].Name);
        var saved = await fixture.Store.SaveServersAsync(initial.Revision, [new(0, "Renamed", "changed.invalid:25565"), new(null, "Added", "[::1]:25565")]);
        Assert.HasCount(2, saved.Entries); Assert.AreEqual("Added", saved.Entries[1].Name); Assert.IsNotNull(saved.BackupPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(saved.BackupPath));
        if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(saved.BackupPath));
        var after = await File.ReadAllBytesAsync(fixture.ServersPath); Assert.AreNotEqual(initial.Revision, saved.Revision);
        var parsed = BoundedNbt.Read(after); Assert.AreEqual(gzip ? NbtCompression.Gzip : NbtCompression.Plain, parsed.Compression);
        Assert.AreEqual("keep-root", parsed.Root.Find("futureRoot")!.Value);
        var list = (NbtList)parsed.Root.Find("servers")!.Value;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255 }, (byte[])list.Items[0].Find("icon")!.Value);
        Assert.AreEqual("retained", list.Items[0].Find("custom")!.Find("value")!.Value);
        Assert.IsNull(list.Items[1].Find("icon"));
        var removed = await fixture.Store.SaveServersAsync(saved.Revision, [new(1, "Added", "[::1]:25565")]);
        Assert.HasCount(1, removed.Entries); Assert.AreEqual("Added", removed.Entries[0].Name);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Game, ".shou-world-*.tmp").Any());
    }

    [TestMethod]
    public async Task StaleRevisionAndCorruptOrAmbiguousNbtNeverOverwriteOrInventSuccess()
    {
        var fixture = new Fixture(); await File.WriteAllBytesAsync(fixture.ServersPath, Servers());
        var read = await fixture.Store.GetServersAsync(); var changed = Servers("External change");
        await File.WriteAllBytesAsync(fixture.ServersPath, changed);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveServersAsync(read.Revision, []));
        CollectionAssert.AreEqual(changed, await File.ReadAllBytesAsync(fixture.ServersPath));
        Assert.AreEqual(1, Directory.GetFiles(fixture.Game).Length);
        var broken = Convert.FromHexString("0A0000090007736572766572730AFFFFFFFF00"); await File.WriteAllBytesAsync(fixture.ServersPath, broken);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.GetServersAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.SaveServersAsync(Hash(broken), []));
        CollectionAssert.AreEqual(broken, await File.ReadAllBytesAsync(fixture.ServersPath));
        var duplicate = Compound("", StringTag("future", "kept"), ListTag("servers", 10, []), ListTag("servers", 10, []));
        await File.WriteAllBytesAsync(fixture.ServersPath, duplicate);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.SaveServersAsync(Hash(duplicate), []));
        CollectionAssert.AreEqual(duplicate, await File.ReadAllBytesAsync(fixture.ServersPath));
    }

    [TestMethod]
    public async Task ReadOnlyServersRemainUntouchedWithoutCreatingBackupOrStagingFiles()
    {
        var fixture = new Fixture(); var bytes = Servers(); await File.WriteAllBytesAsync(fixture.ServersPath, bytes);
        var read = await fixture.Store.GetServersAsync();
        File.SetAttributes(fixture.ServersPath, File.GetAttributes(fixture.ServersPath) | FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveServersAsync(read.Revision, []));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.ServersPath));
            Assert.AreEqual(1, Directory.GetFiles(fixture.Game).Length);
        }
        finally { File.SetAttributes(fixture.ServersPath, File.GetAttributes(fixture.ServersPath) & ~FileAttributes.ReadOnly); }
    }

    [TestMethod]
    public async Task WorldMetadataUsesCurrentAndLegacySeedWithoutChangingLevelBytes()
    {
        var fixture = new Fixture(); var modern = fixture.World("modern", World("Modern world", true)); var legacy = fixture.World("legacy", World("Legacy world", false));
        var modernBytes = await File.ReadAllBytesAsync(modern); var legacyBytes = await File.ReadAllBytesAsync(legacy);
        fixture.World("damaged", [10, 0, 0, 8]);
        var worlds = await fixture.Store.GetWorldsAsync(); Assert.HasCount(3, worlds);
        var current = worlds.Single(w => w.DirectoryName == "modern");
        Assert.AreEqual("Modern world", current.Name); Assert.AreEqual(-9223372036854775000L, current.Seed);
        Assert.AreEqual("1.21.1", current.Version); Assert.AreEqual(3955, current.DataVersion); Assert.AreEqual(1, current.GameMode); Assert.IsTrue(current.Hardcore);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), current.LastPlayed);
        Assert.AreEqual(123456789L, worlds.Single(w => w.DirectoryName == "legacy").Seed);
        Assert.IsNotNull(worlds.Single(w => w.DirectoryName == "damaged").Error);
        CollectionAssert.AreEqual(modernBytes, await File.ReadAllBytesAsync(modern)); CollectionAssert.AreEqual(legacyBytes, await File.ReadAllBytesAsync(legacy));
    }

    [TestMethod]
    public async Task DatapackImportCopiesValidatedZipWithoutChangingEnableStateOrExistingPacks()
    {
        var fixture = new Fixture(); var level = fixture.World("world", World("Pack world", true)); var originalLevel = await File.ReadAllBytesAsync(level);
        var zip = Path.Combine(fixture.Root, "fixture.zip"); var bytes = Zip(("pack.mcmeta", "{\"pack\":{\"pack_format\":48,\"description\":\"fixture\"}}"), ("data/example/function/tick.mcfunction", "say explicit fixture"));
        await File.WriteAllBytesAsync(zip, bytes);
        var imported = await fixture.Store.ImportDatapackAsync("world", zip);
        Assert.AreEqual("fixture.zip", imported.Name); StringAssert.Contains(imported.Status, "尚未修改");
        var listed = await fixture.Store.GetDatapacksAsync("world"); Assert.HasCount(1, listed);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Game, "saves/world/datapacks/fixture.zip")));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.ImportDatapackAsync("world", zip));
        CollectionAssert.AreEqual(originalLevel, await File.ReadAllBytesAsync(level));
    }

    [TestMethod]
    public async Task ZipTraversalLinksChecksumAndExpansionBombsAreRejectedBeforeDestinationCreation()
    {
        var fixture = new Fixture(new(MaxPackExpandedBytes: 32768, MaxPackEntries: 3, MaxPackEntryBytes: 16384)); fixture.World("world", World("Safe world", true));
        var cases = new List<byte[]>
        {
            Zip(("pack.mcmeta", "{\"pack\":{}}"), ("../escape.txt", "outside")),
            Zip(("pack.mcmeta", "{\"pack\":{}}"), ("data/huge", new string('0', 65536))),
            Zip(("pack.mcmeta", "{\"pack\":{}}"), ("PACK.MCMETA", "duplicate")),
            Zip(("readme.txt", "no metadata"))
        };
        var corrupt = Zip(("pack.mcmeta", "{\"pack\":{}}"));
        var central = FindSignature(corrupt, 0x02014b50); corrupt[central + 16] ^= 1; cases.Add(corrupt);
        var forgedCount = Zip(("pack.mcmeta", "{\"pack\":{}}"), ("data/a", "a"), ("data/b", "b"), ("data/c", "c"));
        var end = FindSignature(forgedCount, 0x06054b50); BinaryPrimitives.WriteUInt16LittleEndian(forgedCount.AsSpan(end + 8), 1); BinaryPrimitives.WriteUInt16LittleEndian(forgedCount.AsSpan(end + 10), 1); cases.Add(forgedCount);
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("pack.mcmeta"); entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write("{\"pack\":{}}");
            }
            cases.Add(memory.ToArray());
        }
        for (var index = 0; index < cases.Count; index++)
        {
            var path = Path.Combine(fixture.Root, "bad-" + index + ".zip"); await File.WriteAllBytesAsync(path, cases[index]);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ImportDatapackAsync("world", path));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Game, "saves/world/datapacks")));
        }
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Game, "saves/escape.txt")));
    }

    [TestMethod, TestCategory("WorldPosixLinks")]
    public async Task LinksTraversalAndReplacedDirectoryCannotReadOrOverwriteIndependentFiles()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Native Windows link/reparse fixtures require the Windows gate.");
        var fixture = new Fixture(); var protectedFile = Path.Combine(fixture.Root, "protected.fixture"); var protectedBytes = Servers("Protected"); await File.WriteAllBytesAsync(protectedFile, protectedBytes);
        File.CreateSymbolicLink(fixture.ServersPath, protectedFile);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.GetServersAsync());
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveServersAsync("missing", [new(null, "Bad", "bad.invalid")]));
        CollectionAssert.AreEqual(protectedBytes, await File.ReadAllBytesAsync(protectedFile));
        File.Delete(fixture.ServersPath);
        await using var runner = new ProcessRunner();
        var hardLink = await runner.RunAsync(new("/bin/ln", [protectedFile, fixture.ServersPath], fixture.Root, Timeout: TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, hardLink.ExitCode, hardLink.StandardError);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.GetServersAsync());
        CollectionAssert.AreEqual(protectedBytes, await File.ReadAllBytesAsync(protectedFile));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.GetDatapacksAsync("../outside"));
        var moved = fixture.Game + "-moved"; Directory.Move(fixture.Game, moved); Directory.CreateDirectory(fixture.Game);
        await File.WriteAllBytesAsync(fixture.ServersPath, protectedBytes);
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SaveServersAsync(Hash(protectedBytes), []));
        CollectionAssert.AreEqual(protectedBytes, await File.ReadAllBytesAsync(fixture.ServersPath));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ServiceRequiresExplicitSelectedIsolatedInstanceAndDoesNotUseCredentials(bool isolated)
    {
        var downloader = new NoDownloads(); var native = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        var credentials = new NoCredentials(); var platform = native with { Credentials = credentials, Paths = new FixturePaths(native.Paths, isolated) };
        using var lease = platform.DataLock.Acquire(platform.Paths); using var http = new HttpClient(new NoNetwork());
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        try
        {
            var first = await service.CreateInstanceAsync("First explicit fixture", "1.21.1"); var second = await service.CreateInstanceAsync("Second explicit fixture", "1.21.1");
            var worlds = (IInstanceWorldService)service;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => worlds.SaveInstanceServersAsync(first.Id, "missing", [new(null, "Rejected", "rejected.invalid")]));
            Assert.IsFalse(File.Exists(Path.Combine(first.GameDirectory, "servers.dat")));
            if (!isolated)
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => worlds.SaveInstanceServersAsync(second.Id, "missing", [new(null, "Rejected", "rejected.invalid")]));
                Assert.IsFalse(File.Exists(Path.Combine(second.GameDirectory, "servers.dat"))); Assert.AreEqual(0, credentials.Calls); return;
            }
            var saved = await worlds.SaveInstanceServersAsync(second.Id, "missing", [new(null, "Explicit", "example.invalid:25565")]);
            Assert.HasCount(1, saved.Entries); Assert.IsTrue(File.Exists(Path.Combine(second.GameDirectory, "servers.dat")));
            Assert.AreEqual(0, credentials.Calls); Assert.IsEmpty(service.Snapshot.Accounts);
        }
        finally { await ((ProcessRunner)native.Processes).DisposeAsync(); }
    }

    private sealed class Fixture
    {
        public string Root { get; } = TestFiles.Root();
        public string Game { get; }
        public string ServersPath => Path.Combine(Game, "servers.dat");
        public InstanceWorldStore Store { get; }
        public Fixture(InstanceWorldLimits? limits = null) { Game = Path.Combine(Root, "instance"); Directory.CreateDirectory(Game); Store = new(Root, Game, limits: limits); }
        public string World(string name, byte[] bytes) { var directory = Path.Combine(Game, "saves", name); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "level.dat"); File.WriteAllBytes(path, bytes); return path; }
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

    // Minimal fixture assembler with explicitly known ASCII field names; no production NBT writer is used here.
    private static byte[] Compound(string name, params byte[][] children) => Tag(10, name, children.SelectMany(c => c).Append((byte)0).ToArray());
    private static byte[] StringTag(string name, string value) => Tag(8, name, Text(value));
    private static byte[] IntTag(string name, int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return Tag(3, name, bytes); }
    private static byte[] LongTag(string name, long value) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return Tag(4, name, bytes); }
    private static byte[] ListTag(string name, byte type, byte[][] payloads)
    { var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, payloads.Length); return Tag(9, name, new[] { type }.Concat(size).Concat(payloads.SelectMany(p => p)).ToArray()); }
    private static byte[] Tag(byte type, string name, byte[] payload) => new[] { type }.Concat(Text(name)).Concat(payload).ToArray();
    private static byte[] Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); var size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length); return size.Concat(bytes).ToArray(); }
    private static byte[] Servers(string firstName = "First") => Compound("", StringTag("futureRoot", "keep-root"), ListTag("servers", 10,
        [Compound("", StringTag("name", firstName), StringTag("ip", "first.invalid"), Tag(7, "icon", [0, 0, 0, 4, 1, 2, 3, 255]), Compound("custom", StringTag("value", "retained")))[3..],
         Compound("", StringTag("name", "Second"), StringTag("ip", "second.invalid"))[3..]]));
    private static byte[] World(string name, bool modern) => WpfWorldNbtTests.Gzip(Compound("", Compound("Data", StringTag("LevelName", name),
        modern ? Compound("WorldGenSettings", LongTag("seed", -9223372036854775000L)) : LongTag("RandomSeed", 123456789L),
        Compound("Version", StringTag("Name", "1.21.1")), IntTag("DataVersion", 3955), IntTag("GameType", 1), Tag(1, "hardcore", [1]), LongTag("LastPlayed", 1700000000000),
        Compound("DataPacks", ListTag("Enabled", 8, [Text("vanilla")]), ListTag("Disabled", 8, [])))));
    private static byte[] Zip(params (string Name, string Data)[] entries)
    {
        using var memory = new MemoryStream(); using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var pair in entries) { var entry = archive.CreateEntry(pair.Name, CompressionLevel.Optimal); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(pair.Data); }
        return memory.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static int FindSignature(byte[] bytes, uint signature) { for (var i = 0; i < bytes.Length - 4; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == signature) return i; throw new AssertFailedException("ZIP central header missing from fixture."); }
}
