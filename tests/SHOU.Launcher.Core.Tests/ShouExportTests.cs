using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ShouExportTests
{
    [TestMethod, DataRow("success"), DataRow("persist-failure"), DataRow("cancel")]
    public async Task ExportImportPreservesExactPayloadAndPublishesOnlyAfterCommit(string scenario)
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "中文 实例"); Directory.CreateDirectory(Path.Combine(game, "config"));
        await File.WriteAllTextAsync(Path.Combine(game, "config/client.json"), "{\"fixture\":true}");
        await File.WriteAllTextAsync(Path.Combine(game, "config/account-token.json"), "synthetic-do-not-export");
        Directory.CreateDirectory(Path.Combine(game, "saves/world")); await File.WriteAllTextAsync(Path.Combine(game, "saves/world/fixture.txt"), "world-data");
        var archive = await InstanceWorkspace.ExportAsync(new("fixture", "中文 实例", "1.21.1", game, Loader: "vanilla", GameVersionId: "1.21.1"), Path.Combine(root, "exports"), false, default);
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Local vanilla import must not contact network.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(Path.Combine(root, "data")), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        var inspection = await service.InspectLocalPackAsync(archive); Assert.IsTrue(inspection.CanImport); Assert.AreEqual(LocalPackFormat.ShouExport, inspection.Format);
        CollectionAssert.AreEqual(new[] { "config/client.json" }, inspection.LocalFiles!.ToArray());
        var request = new LocalPackImportRequest(archive, inspection.ArchiveSha256, "roundtrip");
        if (scenario == "persist-failure") { Directory.CreateDirectory(Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json")); await TestFiles.AssertDirectoryCommitFailureAsync(() => service.ImportLocalPackAsync(request)); }
        else if (scenario == "cancel") { using var ct = new CancellationTokenSource(); ct.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => service.ImportLocalPackAsync(request, ct.Token)); }
        else
        {
            var result = await service.ImportLocalPackAsync(request);
            Assert.AreEqual("{\"fixture\":true}", await File.ReadAllTextAsync(Path.Combine(result.Instance.GameDirectory, "config/client.json")));
            Assert.AreNotEqual(game, result.Instance.GameDirectory); Assert.AreEqual(1, result.InstalledFiles); Assert.IsFalse(result.Instance.Installed);
            Assert.HasCount(1, (await ContentInstallFiles.ReadManifestAsync(result.Instance)).Files); return;
        }
        Assert.IsEmpty(service.Snapshot.Instances); Assert.IsEmpty(Directory.GetDirectories(platform.Paths.InstancesDirectory));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task FailedOrCancelledComponentPreparationRetainsInstanceIdentityAndFiles(bool cancel)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new IOException("Controlled unavailable metadata")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        var instance = await service.CreateInstanceAsync("control", "1.21.1"); Directory.CreateDirectory(Path.Combine(instance.GameDirectory, "config"));
        var file = Path.Combine(instance.GameDirectory, "config/client.json"); await File.WriteAllTextAsync(file, "original");
        using var ct = new CancellationTokenSource(); if (cancel) ct.Cancel();
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => service.ChangeInstanceLoaderAsync(instance.Id, "fabric", "0.16.14", ct.Token));
        else await Assert.ThrowsAsync<AggregateException>(() => service.ChangeInstanceLoaderAsync(instance.Id, "fabric", "0.16.14", ct.Token));
        Assert.AreEqual(instance, service.Snapshot.Instances.Single()); Assert.AreEqual("original", await File.ReadAllTextAsync(file));
    }
    [TestMethod, DataRow("hash"), DataRow("extra"), DataRow("path")]
    public async Task ChangedOrUnlistedArchivePayloadIsRejectedBeforeImport(string mutation)
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "config")); await File.WriteAllTextAsync(Path.Combine(game, "config/client.json"), "original");
        var archive = await InstanceWorkspace.ExportAsync(new("fixture", "fixture", "1.21.1", game, GameVersionId: "1.21.1"), Path.Combine(root, "export"), false, default);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            if (mutation == "extra") { using var writer = new StreamWriter(zip.CreateEntry("config/unlisted.json").Open()); writer.Write("extra"); }
            else if (mutation == "hash") { zip.GetEntry("config/client.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("config/client.json").Open()); writer.Write("modified"); }
            else { var entry = zip.GetEntry("shou-instance.json")!; JsonNode json; using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!; json["Files"]![0]!["Path"] = "../escape"; entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("shou-instance.json").Open()); writer.Write(json.ToJsonString()); }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalPackArchive().ReadAsync(archive));
        Assert.IsFalse(File.Exists(Path.Combine(root, "escape")));
    }
}
