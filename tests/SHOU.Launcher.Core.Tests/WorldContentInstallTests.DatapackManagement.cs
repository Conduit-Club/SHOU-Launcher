using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class WorldContentInstallTests
{
    private static string Packs(Fixture f) => Path.Combine(f.World, "datapacks");
    private static string PackManifest(Fixture f) => Path.Combine(f.World, ".shou-content-manifest.json");
    private static NbtTag PackList(string name, IEnumerable<string> values) => new(NbtType.List, name, new NbtList(NbtType.String, values.Select(v => new NbtTag(NbtType.String, "", v)).ToArray()));
    private static async Task PackListsAsync(Fixture f, string[] enabled, string[]? disabled = null)
    {
        var document = BoundedNbt.Read(await File.ReadAllBytesAsync(f.Level)); var data = document.Root.Find("Data")!;
        var children = data.Children.Where(t => t.Name != "DataPacks").Concat(new NbtTag[] { new(NbtType.Compound, "DataPacks", new NbtTag[] { PackList("Enabled", enabled), PackList("Disabled", disabled ?? []), new(NbtType.String, "FutureList", "keep") }) }).ToArray();
        var root = document.Root.Children.Where(t => t.Name != "Data").Append(data with { Value = children }).ToArray();
        await File.WriteAllBytesAsync(f.Level, BoundedNbt.Write(document with { Root = document.Root with { Value = root } }));
    }
    private static string[] StoredPackList(Fixture f, string name) => ((NbtList)BoundedNbt.Read(File.ReadAllBytes(f.Level)).Root.Find("Data")!.Find("DataPacks")!.Find(name)!.Value).Items.Select(t => (string)t.Value).ToArray();
    private static async Task InstallTrackedAsync(Fixture f, string id, string function)
    {
        f.Catalog.Add(id, f.Pack(function, "old-" + id)); var preview = await f.Service.PreviewWorldContentAsync(f.Request(id)); await f.Service.InstallWorldContentAsync(preview.Id);
    }
    private static async Task<WorldDatapackUpdateRequest> UpdateRequestAsync(Fixture f, string name, string? version = null, string? local = null)
    {
        var options = await f.Service.GetWorldDatapackUpdateOptionsAsync(f.Instance.Id, "现有世界", "file/" + name);
        return new(f.Instance.Id, "现有世界", options.WorkspaceRevision, options.Selection, version, version is null ? null : f.Catalog.Files[version].FileName, local);
    }
    private static string[] ActivePayloadFiles(Fixture f) => Directory.Exists(Packs(f)) ? Directory.GetFiles(Packs(f), "*", SearchOption.AllDirectories).Order().Select(p => Path.GetRelativePath(Packs(f), p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray() : [];
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task DatapackOrderWritesActualEnabledListPreservesBuiltinSlotsAndReloads(bool modern)
    {
        await using var f = await Fixture.Create(modern); Directory.CreateDirectory(Packs(f));
        foreach (var name in new[] { "a", "b", "c" }) await File.WriteAllBytesAsync(Path.Combine(Packs(f), name + ".zip"), f.Pack("shared", name));
        await PackListsAsync(f, ["vanilla", "file/a.zip", "mod:preserved", "file/b.zip", "file/c.zip"], ["file/future.zip"]);
        var original = await File.ReadAllBytesAsync(f.Level); var payload = ActivePayloadFiles(f);
        var order = await f.Service.GetWorldDatapackOrderAsync(f.Instance.Id, "现有世界"); Assert.IsTrue(order.Warnings.Count > 0);
        var receipt = await f.Service.SaveWorldDatapackOrderAsync(f.Instance.Id, "现有世界", order.Revision, order.Items.Reverse().ToArray());
        CollectionAssert.AreEqual(new[] { "vanilla", "file/c.zip", "mod:preserved", "file/b.zip", "file/a.zip" }, StoredPackList(f, "Enabled"));
        CollectionAssert.AreEqual(new[] { "file/future.zip" }, StoredPackList(f, "Disabled")); CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(receipt.BackupPath!));
        Assert.AreEqual("keep", BoundedNbt.Read(File.ReadAllBytes(f.Level)).Root.Find("Data")!.Find("DataPacks")!.Find("FutureList")!.Value);
        var reloaded = await new InstanceWorldStore(f.Platform.Paths.DataDirectory, f.Instance.GameDirectory).GetDatapackOrderAsync("现有世界");
        CollectionAssert.AreEqual(order.Items.Reverse().Select(i => i.Name).ToArray(), reloaded.Items.Select(i => i.Name).ToArray()); CollectionAssert.AreEqual(payload, ActivePayloadFiles(f));
    }
    [TestMethod, DataRow("missing"), DataRow("duplicate"), DataRow("changed"), DataRow("cancel"), DataRow("readonly")]
    public async Task InvalidOrInterruptedDatapackSortDoesNotWrite(string scenario)
    {
        await using var f = await Fixture.Create(); Directory.CreateDirectory(Packs(f));
        await File.WriteAllBytesAsync(Path.Combine(Packs(f), "a.zip"), f.Pack("a", "a")); await File.WriteAllBytesAsync(Path.Combine(Packs(f), "b.zip"), f.Pack("b", "b"));
        await PackListsAsync(f, ["vanilla", "file/a.zip", "file/b.zip"]); var state = await f.Service.GetWorldDatapackOrderAsync(f.Instance.Id, "现有世界");
        IReadOnlyList<WorldDatapackOrderItem> request = scenario == "missing" ? [state.Items[0]] : scenario == "duplicate" ? [state.Items[0], state.Items[0]] : state.Items.Reverse().ToArray();
        if (scenario == "changed") await File.WriteAllBytesAsync(Path.Combine(Packs(f), "a.zip"), f.Pack("a", "changed"));
        var before = await File.ReadAllBytesAsync(f.Level); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "readonly") File.SetAttributes(f.Level, FileAttributes.ReadOnly);
        try { await Reject(() => f.Service.SaveWorldDatapackOrderAsync(f.Instance.Id, "现有世界", state.Revision, request, cts.Token)); }
        finally { if (scenario == "readonly") File.SetAttributes(f.Level, FileAttributes.Normal); }
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Level));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task OnlineDatapackUpdateRenamesReferencesBacksUpAndPreservesUnknownManifest(bool modern)
    {
        await using var f = await Fixture.Create(modern); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        var marker = JsonNode.Parse(await File.ReadAllBytesAsync(PackManifest(f)))!.AsObject(); marker["Future"] = "keep-root"; marker["Files"]![0]!["FutureFile"] = 42; await File.WriteAllTextAsync(PackManifest(f), marker.ToJsonString());
        var oldPack = File.ReadAllBytes(Path.Combine(Packs(f), "main.zip")); var oldLevel = File.ReadAllBytes(f.Level); var oldManifest = File.ReadAllBytes(PackManifest(f));
        f.Catalog.AddVersion("main", "main-v2", f.Pack("main", "new"));
        var request = await UpdateRequestAsync(f, "main.zip", "main-v2"); var preview = await f.Service.PreviewWorldDatapackUpdateAsync(request);
        CollectionAssert.AreEqual(oldPack, File.ReadAllBytes(Path.Combine(Packs(f), "main.zip"))); CollectionAssert.AreEqual(oldLevel, File.ReadAllBytes(f.Level));
        var result = await f.Service.ApplyWorldDatapackUpdateAsync(preview.Id);
        Assert.IsFalse(File.Exists(Path.Combine(Packs(f), "main.zip"))); Assert.IsTrue(File.Exists(Path.Combine(Packs(f), "main-v2.zip")));
        CollectionAssert.AreEqual(new[] { "vanilla", "file/main-v2.zip" }, StoredPackList(f, "Enabled"));
        CollectionAssert.AreEqual(oldPack, File.ReadAllBytes(Path.Combine(result.BackupDirectory, "payload/main.zip"))); CollectionAssert.AreEqual(oldLevel, File.ReadAllBytes(Path.Combine(result.BackupDirectory, "level.before"))); CollectionAssert.AreEqual(oldManifest, File.ReadAllBytes(Path.Combine(result.BackupDirectory, "manifest.before")));
        var changed = JsonNode.Parse(File.ReadAllBytes(PackManifest(f)))!; Assert.AreEqual("keep-root", (string?)changed["Future"]); Assert.AreEqual(42, (int)changed["Files"]![0]!["FutureFile"]!);
        var options = await f.Service.GetWorldDatapackUpdateOptionsAsync(f.Instance.Id, "现有世界", "file/main-v2.zip"); Assert.AreEqual("main-v2", options.CurrentReleaseId);
        var store = new InstanceWorldStore(f.Platform.Paths.DataDirectory, f.Instance.GameDirectory); Assert.AreEqual("main-v2.zip", (await store.GetDatapackOrderAsync("现有世界")).Items.Single().Name);
        Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-datapack-update-*").Any());
    }
    [TestMethod]
    public async Task DatapackUpdateResolvesAndEnablesNewRequiredDependencyBeforeEnabledRoot()
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        f.Catalog.Add("dependency", f.Pack("dependency", "api")); f.Catalog.AddVersion("main", "v2", f.Pack("main", "new"), [new("required", "dependency", "dependency")]);
        var preview = await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", "v2")); Assert.IsTrue(preview.Changes.Any(c => c.Contains("启用必需依赖")));
        await f.Service.ApplyWorldDatapackUpdateAsync(preview.Id);
        CollectionAssert.AreEqual(new[] { "vanilla", "file/dependency.zip", "file/v2.zip" }, StoredPackList(f, "Enabled"));
        Assert.HasCount(2, JsonNode.Parse(File.ReadAllBytes(PackManifest(f)))!["Files"]!.AsArray());
    }
    [TestMethod, DataRow("resource"), DataRow("pinned"), DataRow("incompatible"), DataRow("disabledDependency")]
    public async Task DatapackUpdateRejectsResourceAndDependencyConflictsBeforeChangingWorld(string scenario)
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        IReadOnlyList<ContentDependency>? dependencies = null;
        if (scenario == "resource") { await InstallTrackedAsync(f, "other", "occupied"); }
        if (scenario == "pinned") { f.Catalog.Add("other", f.Pack("other", "old"), dependencies: [new("required", "main", "main")]); var p = await f.Service.PreviewWorldContentAsync(f.Request("other")); await f.Service.InstallWorldContentAsync(p.Id); }
        if (scenario == "incompatible") { await InstallTrackedAsync(f, "other", "other"); dependencies = [new("incompatible", "other")]; }
        if (scenario == "disabledDependency") { await InstallTrackedAsync(f, "dependency", "api"); await PackListsAsync(f, ["vanilla", "file/main.zip"], ["file/dependency.zip"]); dependencies = [new("required", "dependency", "dependency")]; }
        f.Catalog.AddVersion("main", "v2", f.Pack(scenario == "resource" ? "occupied" : "main", "new"), dependencies);
        var before = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f));
        try { await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", "v2")); Assert.Fail("Conflicting update accepted"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { }
        CollectionAssert.AreEqual(before, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f)));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task ManualZipReplacementSupportsUntrackedFileAndDirectoryWithoutInventingOnlineOrigin(bool folder)
    {
        await using var f = await Fixture.Create(); Directory.CreateDirectory(Packs(f)); var sourceBytes = f.Pack("main", "old");
        var oldName = folder ? "local" : "local.zip";
        if (folder) { var source = await LocalArchiveAsync(sourceBytes); ZipFile.ExtractToDirectory(source, Path.Combine(Packs(f), oldName)); }
        else await File.WriteAllBytesAsync(Path.Combine(Packs(f), oldName), sourceBytes);
        await PackListsAsync(f, ["vanilla"], ["file/" + oldName]); var sourcePath = await LocalArchiveAsync(f.Pack("main", "new"));
        var sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath)); var request = await UpdateRequestAsync(f, oldName, local: sourcePath);
        var preview = await f.Service.PreviewWorldDatapackUpdateAsync(request); var receipt = await f.Service.ApplyWorldDatapackUpdateAsync(preview.Id);
        CollectionAssert.AreEqual(new[] { "file/地图.zip" }, StoredPackList(f, "Disabled")); CollectionAssert.AreEqual(new[] { "vanilla" }, StoredPackList(f, "Enabled"));
        Assert.IsFalse(File.Exists(PackManifest(f))); Assert.IsTrue(File.Exists(Path.Combine(Packs(f), "地图.zip"))); Assert.IsTrue(Path.Exists(Path.Combine(receipt.BackupDirectory, "payload", oldName)));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(File.ReadAllBytes(sourcePath)));
    }
    [TestMethod, DataRow("manifestReadonly"), DataRow("cancel"), DataRow("stale"), DataRow("source"), DataRow("target"), DataRow("network")]
    public async Task DatapackUpdateFailureAndCancellationRetainOldFilesReferencesAndManifest(string scenario)
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        f.Catalog.AddVersion("main", "v2", f.Pack("main", "new")); var request = await UpdateRequestAsync(f, "main.zip", "v2");
        if (scenario == "network")
        {
            f.Handler.BeforeArchive = () => throw new IOException("controlled offline transfer"); var original = ActivePayloadFiles(f);
            await Reject(() => f.Service.PreviewWorldDatapackUpdateAsync(request)); CollectionAssert.AreEqual(original, ActivePayloadFiles(f)); return;
        }
        var preview = await f.Service.PreviewWorldDatapackUpdateAsync(request);
        if (scenario == "stale") await PackListsAsync(f, ["vanilla"], ["file/main.zip"]);
        if (scenario == "source") { var file = f.Catalog.Files["v2"]; await File.WriteAllBytesAsync(Path.Combine(f.Platform.Paths.CacheDirectory, "content", file.Sha1, file.FileName), f.Pack("main", "changed cache")); }
        if (scenario == "target") await File.WriteAllTextAsync(Path.Combine(Packs(f), "v2.zip"), "foreign");
        var before = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f)); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        if (scenario == "manifestReadonly") File.SetAttributes(PackManifest(f), FileAttributes.ReadOnly);
        try { await Reject(() => f.Service.ApplyWorldDatapackUpdateAsync(preview.Id, cts.Token)); }
        finally { if (scenario == "manifestReadonly") File.SetAttributes(PackManifest(f), FileAttributes.Normal); }
        CollectionAssert.AreEqual(before, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f)));
        Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-datapack-update-*").Any());
    }
    [TestMethod]
    public async Task LocalManualUpdateRejectsWrongFormatAndChangedSource()
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        var bad = await LocalArchiveAsync(Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":99}}" }));
        var request = await UpdateRequestAsync(f, "main.zip", local: bad); await Reject(() => f.Service.PreviewWorldDatapackUpdateAsync(request));
        var good = await LocalArchiveAsync(f.Pack("main", "new")); var preview = await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", local: good));
        await File.WriteAllBytesAsync(good, f.Pack("main", "changed source")); var before = ActivePayloadFiles(f);
        await Reject(() => f.Service.ApplyWorldDatapackUpdateAsync(preview.Id)); CollectionAssert.AreEqual(before, ActivePayloadFiles(f));
    }
    [TestMethod]
    public async Task DatapackUpdateCancelledAfterOriginalWasMovedRestoresOriginalAndMetadata()
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]);
        var bytes = new byte[16 * 1024 * 1024]; RandomNumberGenerator.Fill(bytes);
        f.Catalog.AddVersion("main", "v2", ZipBytes(new() { ["pack.mcmeta"] = Encoding.UTF8.GetBytes("{\"pack\":{\"pack_format\":48}}"), ["data/test/function/main.mcfunction"] = bytes }));
        var preview = await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", "v2"));
        var before = ActivePayloadFiles(f); var level = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f)); using var cts = new CancellationTokenSource(); var reached = new TaskCompletionSource();
        using var watcher = new FileSystemWatcher(f.World, "main.zip") { IncludeSubdirectories = true };
        watcher.Created += (_, e) => { if (e.FullPath.Contains(Path.DirectorySeparatorChar + "payload" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { reached.TrySetResult(); cts.Cancel(); } }; watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.ApplyWorldDatapackUpdateAsync(preview.Id, cts.Token)); await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(before, ActivePayloadFiles(f)); CollectionAssert.AreEqual(level, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f)));
        Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-datapack-update-*").Any());
    }
    [TestMethod]
    public async Task FailedOnlineVersionReadStillAllowsManualChoiceWithoutFakingUpdates()
    {
        await using var f = await Fixture.Create(); await InstallTrackedAsync(f, "main", "main"); await PackListsAsync(f, ["vanilla", "file/main.zip"]); f.Catalog.FailVersionLists = true;
        var options = await f.Service.GetWorldDatapackUpdateOptionsAsync(f.Instance.Id, "现有世界", "file/main.zip");
        Assert.HasCount(0, options.Choices); Assert.IsNotNull(options.Notice); Assert.AreEqual("main", options.ProjectId);
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task UpdatedIdentityAndSavedOrderPersistAfterLauncherServiceShutdownAndReinitialize(bool modern)
    {
        await using var f = await Fixture.Create(modern); await InstallTrackedAsync(f, "main", "main"); await InstallTrackedAsync(f, "other", "other");
        await PackListsAsync(f, ["vanilla", "file/main.zip", "file/other.zip"]); f.Catalog.AddVersion("main", "main-v2", f.Pack("main", "new"));
        var preview = await f.Service.PreviewWorldDatapackUpdateAsync(await UpdateRequestAsync(f, "main.zip", "main-v2")); await f.Service.ApplyWorldDatapackUpdateAsync(preview.Id);
        var order = await f.Service.GetWorldDatapackOrderAsync(f.Instance.Id, "现有世界"); await f.Service.SaveWorldDatapackOrderAsync(f.Instance.Id, "现有世界", order.Revision, order.Items.Reverse().ToArray());
        var bytes = File.ReadAllBytes(f.Level); var manifest = File.ReadAllBytes(PackManifest(f)); var packs = ActivePayloadFiles(f);
        await f.RestartServiceAsync(); Assert.AreEqual(f.Instance.Id, f.Service.Snapshot.SelectedInstanceId);
        var reopened = await f.Service.GetWorldDatapackOrderAsync(f.Instance.Id, "现有世界"); CollectionAssert.AreEqual(new[] { "other.zip", "main-v2.zip" }, reopened.Items.Select(p => p.Name).ToArray());
        var origin = await f.Service.GetWorldDatapackUpdateOptionsAsync(f.Instance.Id, "现有世界", "file/main-v2.zip"); Assert.AreEqual("main-v2", origin.CurrentReleaseId);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(f.Level)); CollectionAssert.AreEqual(manifest, File.ReadAllBytes(PackManifest(f))); CollectionAssert.AreEqual(packs, ActivePayloadFiles(f));
    }
}
