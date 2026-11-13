using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CurseForgeExportTests
{
    [TestMethod, DataRow("cf"), DataRow("mrpack"), DataRow("shou")]
    public async Task CancellationAfterArchiveCreationRemovesPartialOutput(string format)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Export stays offline.")));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var runner = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        var instance = await service.CreateInstanceAsync("cancel", "1.21.1"); Directory.CreateDirectory(Path.Combine(instance.GameDirectory, "resourcepacks"));
        var payload = new byte[16 * 1024 * 1024]; Random.Shared.NextBytes(payload);
        var source = Path.Combine(instance.GameDirectory, "resourcepacks/controlled.zip"); await File.WriteAllBytesAsync(source, payload);
        var output = Path.Combine(platform.Paths.CacheDirectory, "exports"); Directory.CreateDirectory(output);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var watcher = new FileSystemWatcher(output) { EnableRaisingEvents = true };
        var reached = false;
        watcher.Created += (_, e) => { if (e.Name!.StartsWith(".export-", StringComparison.Ordinal) || e.Name.EndsWith(".tmp", StringComparison.Ordinal)) { reached = true; cancel.Cancel(); } };
        Task Export() => format == "cf" ? service.ExportCurseForgePackAsync(instance.Id, new("cancel", OverridePaths: ["resourcepacks"], BundleInstalledFiles: true), cancel.Token)
            : format == "mrpack" ? service.ExportModrinthPackAsync(instance.Id, new("cancel", OverridePaths: ["resourcepacks"], BundleInstalledFiles: true), cancel.Token)
            : service.ExportInstanceAsync(instance.Id, false, cancel.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(Export);
        Assert.IsTrue(reached); Assert.IsEmpty(Directory.GetFiles(output));
        CollectionAssert.AreEqual(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }
    [TestMethod, DataRow("success"), DataRow("persist-failure"), DataRow("changed-archive")]
    public async Task ExportRoundtripIsExactAndFailedImportPublishesNothing(string scenario)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Vanilla override roundtrip must stay offline.")));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var runner = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        var source = await service.CreateInstanceAsync("中文 CF 源", "1.21.1");
        Directory.CreateDirectory(Path.Combine(source.GameDirectory, "config"));
        await File.WriteAllTextAsync(Path.Combine(source.GameDirectory, "config/client.json"), "public config");
        await File.WriteAllTextAsync(Path.Combine(source.GameDirectory, "config/account-token.json"), "synthetic-private");
        var zip = await service.ExportCurseForgePackAsync(source.Id, new("客户端包", "1", ["config"], true));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.HasCount(3, archive.Entries); Assert.IsNotNull(archive.GetEntry("manifest.json"));
            Assert.IsNotNull(archive.GetEntry("overrides/config/client.json"));
        }
        var inspect = await service.InspectLocalPackAsync(zip); Assert.IsTrue(inspect.CanImport);
        var before = service.Snapshot.Instances.ToArray(); var directories = Directory.GetDirectories(platform.Paths.InstancesDirectory).Order().ToArray();
        if (scenario == "changed-archive")
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) { using var writer = new StreamWriter(archive.CreateEntry("overrides/config/new.json").Open()); writer.Write("changed"); }
            await Assert.ThrowsAsync<IOException>(() => service.ImportLocalPackAsync(new(zip, inspect.ArchiveSha256, "回导")));
        }
        else if (scenario == "persist-failure")
        {
            // Preserve the previous valid state file before introducing a controlled publication fault.
            var state = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json");
            File.Move(state, state + ".fixture-original"); Directory.CreateDirectory(state);
            await TestFiles.AssertDirectoryCommitFailureAsync(() => service.ImportLocalPackAsync(new(zip, inspect.ArchiveSha256, "回导")));
        }
        else
        {
            var imported = await service.ImportLocalPackAsync(new(zip, inspect.ArchiveSha256, "回导"));
            Assert.AreNotEqual(source.Id, imported.Instance.Id);
            Assert.AreEqual("public config", await File.ReadAllTextAsync(Path.Combine(imported.Instance.GameDirectory, "config/client.json")));
            Assert.AreEqual(2, imported.InstalledFiles); return;
        }
        CollectionAssert.AreEqual(before, service.Snapshot.Instances.ToArray());
        CollectionAssert.AreEqual(directories, Directory.GetDirectories(platform.Paths.InstancesDirectory).Order().ToArray());
    }

    [TestMethod]
    public async Task CurseForgeReferencesKeepExactIdentityAndRejectModifiedSource()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Export must not contact API.")));
        var officialMismatch = false;
        using var api = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Assert.AreEqual("api.curseforge.com", request.RequestUri!.Host);
            Assert.AreEqual("synthetic-export-key", request.Headers.GetValues("x-api-key").Single());
            object data = request.RequestUri.AbsolutePath.EndsWith("/1001", StringComparison.Ordinal)
                ? new { id = 1001, modId = 101, gameId = 432, isAvailable = true, fileStatus = 4, fileName = "fixture.jar", fileLength = 3,
                    downloadUrl = "https://edge.forgecdn.net/files/1/2/fixture.jar", hashes = new[] { new { algo = 1, value = Convert.ToHexStringLower(SHA1.HashData(officialMismatch ? [3, 2, 1] : new byte[] { 1, 2, 3 })) } }, gameVersions = new[] { "1.21.1" } }
                : new { id = 101, gameId = 432, classId = 6 };
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { data })));
        }));
        var downloads = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloads);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var runner = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloads, new NetworkOptions(), localPackResolver: new(api, "synthetic-export-key"));
        var source = await service.CreateInstanceAsync("CF refs", "1.21.1"); Directory.CreateDirectory(Path.Combine(source.GameDirectory, "mods"));
        var bytes = new byte[] { 1, 2, 3 }; var path = Path.Combine(source.GameDirectory, "mods/fixture.jar"); await File.WriteAllBytesAsync(path, bytes);
        var file = new ContentInstalledFile("cf-101", "cf-1001", "mod", "mods/fixture.jar", Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.Length);
        await File.WriteAllBytesAsync(Path.Combine(source.GameDirectory, ContentInstallFiles.ManifestName), JsonSerializer.SerializeToUtf8Bytes(new ContentInstallManifest(1, source.Id, [file])));
        var output = await service.ExportCurseForgePackAsync(source.Id, new("refs", OverridePaths: ["mods"], BundleInstalledFiles: true));
        using var archive = ZipFile.OpenRead(output); Assert.HasCount(1, archive.Entries);
        using var json = await JsonDocument.ParseAsync(archive.GetEntry("manifest.json")!.Open());
        Assert.AreEqual(101, json.RootElement.GetProperty("files")[0].GetProperty("projectID").GetInt32());
        Assert.AreEqual(1001, json.RootElement.GetProperty("files")[0].GetProperty("fileID").GetInt32());
        await File.WriteAllBytesAsync(path, [3, 2, 1]);
        await Assert.ThrowsAsync<IOException>(() => service.ExportCurseForgePackAsync(source.Id, new("refs")));
        await File.WriteAllBytesAsync(path, bytes); officialMismatch = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportCurseForgePackAsync(source.Id, new("refs")));
    }

    [TestMethod, DataRow("forge"), DataRow("neoforge"), DataRow("quilt")]
    public async Task AllPortablePackFormatsRetainLoaderIdentity(string loader)
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "game/config"));
        var instance = new GameInstance("fixture", "fixture", "profile", Path.Combine(root, "game"), Loader: loader, LoaderVersion: "1.2.3-beta", GameVersionId: "1.21.1");
        await File.WriteAllTextAsync(Path.Combine(instance.GameDirectory, "config/public.json"), "{}");
        var shou = await SHOU.Launcher.Core.Instances.InstanceWorkspace.ExportAsync(instance, Path.Combine(root, "shou"), false, default);
        var shouRead = (await new LocalPackArchive().ReadAsync(shou)).Inspection;
        Assert.AreEqual(loader, shouRead.Loader); Assert.AreEqual(instance.LoaderVersion, shouRead.LoaderVersion); Assert.IsTrue(shouRead.CanImport);
        var mr = await new ModrinthPackArchive().ExportAsync(instance, new("fixture", OverridePaths: ["config"], BundleInstalledFiles: true), Path.Combine(root, "mr"));
        var mrRead = await new ModrinthPackArchive().InspectAsync(mr);
        Assert.AreEqual(loader, mrRead.Loader); Assert.IsTrue(mrRead.CanImport);
        // CF parsing must split once; beta versions retain their entire version identity.
        var cf = Path.Combine(root, "cf.zip");
        using (var zip = ZipFile.Open(cf, ZipArchiveMode.Create))
        { await using var entry = zip.CreateEntry("manifest.json").Open(); await JsonSerializer.SerializeAsync(entry, new { manifestType = "minecraftModpack", manifestVersion = 1, name = "fixture", minecraft = new { version = "1.21.1", modLoaders = new[] { new { id = loader + "-" + instance.LoaderVersion, primary = true } } }, files = Array.Empty<object>() }); }
        var cfRead = await new LocalPackArchive().ReadAsync(cf); Assert.IsTrue(cfRead.CanResolveCurseForge);
        Assert.AreEqual(instance.LoaderVersion, cfRead.Inspection.LoaderVersion);
    }
}
