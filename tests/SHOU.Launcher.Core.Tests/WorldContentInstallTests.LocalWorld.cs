using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class WorldContentInstallTests
{
    private static async Task<string> LocalArchiveAsync(byte[] bytes)
    {
        var directory = Path.Combine(TestFiles.Root(), "本地世界 ZIP 中文"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "地图.zip"); await File.WriteAllBytesAsync(path, bytes); return path;
    }
    [TestMethod, DataRow(false, false), DataRow(true, false), DataRow(false, true), DataRow(true, true)]
    public async Task LocalWorldRootAndWrapperPreviewThenImportPreservesSourceAndReloads(bool wrapped, bool modern)
    {
        await using var f = await Fixture.Create(modern); var prefix = wrapped ? "我的世界/" : ""; var level = Level(f.Version, f.DataVersion, "本地世界名称");
        var source = await LocalArchiveAsync(ZipBytes(new() { [prefix + "level.dat"] = level, [prefix + "region/r.0.0.mca"] = Encoding.UTF8.GetBytes("controlled region"), [prefix + "session.lock"] = [1, 2], [prefix + ".DS_Store"] = [0], ["__MACOSX/" + prefix + "._level.dat"] = [1, 2] }));
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(source)); var before = Files(f.Instance.GameDirectory);
        var preview = await f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, "导入 世界"));
        Assert.AreEqual("本地世界名称", preview.WorldName); Assert.AreEqual(f.Version, preview.Installation.GameVersion); Assert.HasCount(2, preview.Installation.Files);
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory));
        var result = await f.Service.ImportLocalWorldAsync(preview.Installation.Id);
        CollectionAssert.AreEqual(level, await File.ReadAllBytesAsync(Path.Combine(result.Destination, "level.dat")));
        Assert.AreEqual("controlled region", await File.ReadAllTextAsync(Path.Combine(result.Destination, "region/r.0.0.mca")));
        Assert.IsFalse(File.Exists(Path.Combine(result.Destination, "session.lock"))); Assert.IsFalse(File.Exists(Path.Combine(result.Destination, ".DS_Store")));
        CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var receipt = await File.ReadAllTextAsync(Path.Combine(result.Destination, ".shou-world-source.json")); Assert.IsFalse(receipt.Contains(source, StringComparison.Ordinal));
        using var record = JsonDocument.Parse(receipt); Assert.AreEqual("local-world", record.RootElement.GetProperty("Kind").GetString()); Assert.AreEqual(preview.ArchiveSha256, record.RootElement.GetProperty("Sha256").GetString());
        var restarted = new InstanceWorldStore(f.Platform.Paths.DataDirectory, f.Instance.GameDirectory); var world = (await restarted.GetWorldsAsync()).Single(w => w.DirectoryName == "导入 世界");
        Assert.AreEqual("本地世界名称", world.Name); Assert.AreEqual(f.DataVersion, world.DataVersion);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.ImportLocalWorldAsync(preview.Installation.Id));
    }
    [TestMethod, DataRow("version"), DataRow("multiple"), DataRow("path"), DataRow("sensitive"), DataRow("mixed"), DataRow("pack"), DataRow("dependency"), DataRow("target")]
    public async Task LocalWorldInvalidArchiveOrConflictRejectsWithoutChangingSaves(string scenario)
    {
        await using var f = await Fixture.Create(); var entries = new Dictionary<string, byte[]> { ["level.dat"] = Level(f.Version, scenario == "version" ? 4903 : f.DataVersion, "controlled") };
        if (scenario == "multiple") entries["another/level.dat"] = entries["level.dat"];
        if (scenario == "path") entries["../outside.txt"] = [1];
        if (scenario == "sensitive") entries["accounts.json"] = [1];
        if (scenario == "mixed") { entries["world/level.dat"] = entries["level.dat"]; entries.Remove("level.dat"); entries["readme.txt"] = [1]; }
        if (scenario == "pack") entries["datapacks/bad.zip"] = Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":99}}" });
        if (scenario == "dependency")
        {
            var data = new NbtTag(NbtType.Compound, "Data", new NbtTag[] { new(NbtType.Int, "DataVersion", 3955), new(NbtType.Compound, "Version", new NbtTag[] { new(NbtType.String, "Name", "1.21.1") }), new(NbtType.Compound, "DataPacks", new NbtTag[] { new(NbtType.List, "Enabled", new NbtList(NbtType.String, [new(NbtType.String, "", "mod:unverified")])) }) });
            entries["level.dat"] = BoundedNbt.Write(new(new(NbtType.Compound, "", new[] { data }), NbtCompression.Gzip));
        }
        var source = await LocalArchiveAsync(ZipBytes(entries)); var before = Files(f.Instance.GameDirectory);
        await Reject(() => f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, scenario == "target" ? "现有世界" : "新世界")));
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory)); Assert.IsFalse(Directory.EnumerateDirectories(f.Platform.Paths.TempDirectory, "world-local-inspect-*").Any());
    }
    [TestMethod, DataRow("changed"), DataRow("cancel"), DataRow("appeared"), DataRow("case"), DataRow("discard")]
    public async Task LocalWorldConfirmRechecksSourceTargetAndCancellation(string scenario)
    {
        await using var f = await Fixture.Create(); var source = await LocalArchiveAsync(ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "controlled") }));
        var preview = await f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, "new"));
        if (scenario == "changed") await File.WriteAllBytesAsync(source, ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "modified valid archive") }));
        if (scenario is "appeared" or "case") { var destination = Path.Combine(f.Instance.GameDirectory, scenario == "case" ? "saves/NEW" : "saves/new"); Directory.CreateDirectory(destination); await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "foreign"); }
        if (scenario == "discard") await f.Service.DiscardLocalWorldImportAsync(preview.Installation.Id);
        var before = Files(f.Instance.GameDirectory); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "discard") await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Service.ImportLocalWorldAsync(preview.Installation.Id));
        else await Reject(() => f.Service.ImportLocalWorldAsync(preview.Installation.Id, cts.Token));
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory)); Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
    }
    [TestMethod]
    public async Task LocalWorldLateCrcFailureRollsBackExtractedFiles()
    {
        await using var f = await Fixture.Create(); var zip = ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "controlled"), ["region/r.0.0.mca"] = Encoding.UTF8.GetBytes("controlled region") });
        var seen = 0; for (var i = 0; i < zip.Length - 46; i++) if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 1 && zip[i + 3] == 2 && ++seen == 2) { zip[i + 16] ^= 0xff; break; }
        var source = await LocalArchiveAsync(zip); var preview = await f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, "new")); var before = Files(f.Instance.GameDirectory);
        await Reject(() => f.Service.ImportLocalWorldAsync(preview.Installation.Id)); CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
    }
    [TestMethod]
    public async Task LocalWorldCancelAfterStagingCreationDoesNotPublishAnything()
    {
        await using var f = await Fixture.Create(); var entries = new Dictionary<string, byte[]> { ["level.dat"] = Level(f.Version, f.DataVersion, "controlled") };
        var region = new byte[16 * 1024 * 1024]; RandomNumberGenerator.Fill(region); entries["region/r.0.0.mca"] = region;
        var source = await LocalArchiveAsync(ZipBytes(entries)); var preview = await f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, "new")); var before = Files(f.Instance.GameDirectory);
        using var cts = new CancellationTokenSource(); var observed = new TaskCompletionSource();
        using var watcher = new FileSystemWatcher(f.Instance.GameDirectory) { NotifyFilter = NotifyFilters.DirectoryName };
        watcher.Created += (_, e) => { if (Path.GetFileName(e.FullPath).StartsWith(".shou-world-install-", StringComparison.Ordinal)) { observed.TrySetResult(); cts.Cancel(); } }; watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.ImportLocalWorldAsync(preview.Installation.Id, cts.Token)); await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory)); Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
        Assert.IsTrue(File.Exists(source));
    }
    [TestMethod]
    public async Task LocalWorldExplicitInstanceTargetDoesNotChangeHomeSelection()
    {
        await using var f = await Fixture.Create(); var other = await f.Service.CreateInstanceAsync("另一个实例", f.Version);
        var selected = f.Service.Snapshot.SelectedInstanceId; var source = await LocalArchiveAsync(ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "controlled") }));
        var preview = await f.Service.PreviewLocalWorldImportAsync(new(f.Instance.Id, source, "new")); await f.Service.ImportLocalWorldAsync(preview.Installation.Id);
        Assert.AreEqual(selected, f.Service.Snapshot.SelectedInstanceId); Assert.IsFalse(Directory.Exists(Path.Combine(other.GameDirectory, "saves/new")));
        Assert.IsTrue(File.Exists(Path.Combine(f.Instance.GameDirectory, "saves/new/level.dat")));
    }
}
