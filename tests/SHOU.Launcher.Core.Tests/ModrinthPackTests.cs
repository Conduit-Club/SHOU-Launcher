using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ModrinthPackTests
{
    private static readonly byte[] Bytes = [1, 4, 9, 16, 25];
    private static JsonObject FileEntry(string path, string client = "required", string server = "unsupported") => new()
    {
        ["path"] = path, ["fileSize"] = Bytes.Length, ["hashes"] = new JsonObject { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Bytes)), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(Bytes)) },
        ["downloads"] = new JsonArray("https://cdn.modrinth.com/data/fixture.bin"), ["env"] = new JsonObject { ["client"] = client, ["server"] = server }
    };
    private static JsonObject Index(JsonArray? files = null, string? loader = null) => new()
    {
        ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = "Local fixture pack", ["versionId"] = "fixture-1",
        ["files"] = files ?? new JsonArray(FileEntry("mods/required.jar")),
        ["dependencies"] = loader is null ? new JsonObject { ["minecraft"] = "1.21.1" } : new JsonObject { ["minecraft"] = "1.21.1", [loader] = "0.16.5" }
    };
    private static string Archive(string root, JsonObject index, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mrpack");
        using var output = File.Create(path); using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open(), new UTF8Encoding(false))) writer.Write(index.ToJsonString());
        foreach (var (name, text) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        return path;
    }
    private static Task Download(ModrinthPackFile file, string target, CancellationToken ct) => System.IO.File.WriteAllBytesAsync(target, Bytes, ct);

    [TestMethod]
    public async Task ClientImportSkipsServerFilesAndUnselectedOptionalAndAppliesClientOverrideLayer()
    {
        var root = TestFiles.Root(); var path = Archive(root, Index(new JsonArray(FileEntry("mods/required.jar"), FileEntry("mods/optional.jar", "optional"), FileEntry("server.jar", "unsupported", "required"))),
            ("overrides/config/game.txt", "common"), ("client-overrides/config/game.txt", "client"), ("server-overrides/server.properties", "server-only"),
            ("overrides/mods/optional.jar", "optional override"), ("client-overrides/server.jar", "server override must remain skipped"));
        var engine = new ModrinthPackArchive(); var inspection = await engine.InspectAsync(path); var target = Path.Combine(root, "stage"); var calls = new List<string>();
        await engine.StageAsync(path, inspection.ArchiveSha256, target, [], async (file, output, ct) => { calls.Add(file.Path); await Download(file, output, ct); });
        CollectionAssert.AreEqual(new[] { "mods/required.jar" }, calls); Assert.AreEqual("client", await System.IO.File.ReadAllTextAsync(Path.Combine(target, "config/game.txt")));
        Assert.IsFalse(System.IO.File.Exists(Path.Combine(target, "server.jar"))); Assert.IsFalse(System.IO.File.Exists(Path.Combine(target, "server.properties")));
        Assert.IsFalse(System.IO.File.Exists(Path.Combine(target, "mods/optional.jar")));
        await engine.StageAsync(path, inspection.ArchiveSha256, Path.Combine(root, "selected"), ["mods/optional.jar"], Download);
        Assert.IsTrue(System.IO.File.Exists(Path.Combine(root, "selected/mods/optional.jar")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => engine.StageAsync(path, inspection.ArchiveSha256, Path.Combine(root, "invalid"), ["server.jar"], Download));
    }

    [TestMethod]
    [DataRow("traversal")]
    [DataRow("duplicate")]
    [DataRow("link")]
    [DataRow("json")]
    [DataRow("quantity")]
    public async Task UnsafeZipPathsAndAmbiguousManifestsAreRejectedBeforeDownload(string scenario)
    {
        var root = TestFiles.Root(); var path = Archive(root, Index(), scenario == "quantity" ? Enumerable.Range(0, ModrinthPackArchive.MaximumFiles + 1).Select(i => ("overrides/config/file" + i + ".txt", "x")).ToArray()
            : scenario == "traversal" ? [("overrides/../escape", "bad")] : scenario == "duplicate" ? [("overrides/config/A.txt", "a"), ("overrides/config/a.txt", "b")] : [("overrides/config/link", "target")]);
        if (scenario is "link" or "json")
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            if (scenario == "link") zip.GetEntry("overrides/config/link")!.ExternalAttributes = 0xA1FF << 16;
            else { zip.GetEntry("modrinth.index.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()); writer.Write("{\"name\":\"a\",\"name\":\"b\"}"); }
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ModrinthPackArchive().InspectAsync(path));
        Assert.IsFalse(System.IO.File.Exists(Path.Combine(root, "escape")));
    }

    [TestMethod]
    public async Task UnapprovedDownloadHostAndUnsupportedLoaderHaveExplicitCapabilityBoundaries()
    {
        var root = TestFiles.Root(); var index = Index(); index["files"]![0]!["downloads"] = new JsonArray("https://example.invalid/file.jar");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new ModrinthPackArchive().InspectAsync(Archive(root, index)));
        var unsupported = Archive(root, Index(loader: "forge")); var inspection = await new ModrinthPackArchive().InspectAsync(unsupported);
        Assert.IsFalse(inspection.CanImport); StringAssert.Contains(inspection.CapabilityReason!, "forge");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new ModrinthPackArchive().StageAsync(unsupported, inspection.ArchiveSha256, Path.Combine(root, "unsupported"), [], Download));
    }

    [TestMethod]
    public async Task ChangedArchiveAndIncorrectSha512CannotPassStageValidation()
    {
        var root = TestFiles.Root(); var engine = new ModrinthPackArchive(); var path = Archive(root, Index()); var inspection = await engine.InspectAsync(path);
        await System.IO.File.AppendAllTextAsync(path, "changed after inspection");
        await Assert.ThrowsAsync<IOException>(() => engine.StageAsync(path, inspection.ArchiveSha256, Path.Combine(root, "changed"), [], Download));
        var invalid = Index(); invalid["files"]![0]!["hashes"]!["sha512"] = new string('0', 128); var badPath = Archive(root, invalid); var bad = await engine.InspectAsync(badPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => engine.StageAsync(badPath, bad.ArchiveSha256, Path.Combine(root, "bad"), [], Download));
    }

    [TestMethod]
    public async Task ExportRoundTripsRemoteHashesClientEnvironmentAndExplicitLocalConfiguration()
    {
        var root = TestFiles.Root(); var engine = new ModrinthPackArchive(); var path = Archive(root, Index(), ("client-overrides/config/game.txt", "config value"));
        var inspection = await engine.InspectAsync(path); var game = Path.Combine(root, "game"); await engine.StageAsync(path, inspection.ArchiveSha256, game, [], Download);
        var manifestPath = Path.Combine(game, ContentInstallFiles.ManifestName);
        var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await System.IO.File.ReadAllBytesAsync(manifestPath))!;
        await System.IO.File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { InstanceId = "instance" }));
        var instance = new GameInstance("instance", "Export fixture", "1.21.1", game);
        var exported = await engine.ExportAsync(instance, new("Export fixture", OverridePaths: ["config"]), Path.Combine(root, "exports"));
        var after = await engine.InspectAsync(exported); Assert.HasCount(1, after.Files); Assert.AreEqual("unsupported", after.Files[0].Server);
        await engine.StageAsync(exported, after.ArchiveSha256, Path.Combine(root, "roundtrip"), [], Download);
        Assert.AreEqual("config value", await System.IO.File.ReadAllTextAsync(Path.Combine(root, "roundtrip/config/game.txt")));
        CollectionAssert.AreEqual(Bytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(root, "roundtrip/mods/required.jar")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => engine.ExportAsync(instance, new("Missing local selection"), Path.Combine(root, "exports")));
    }

    [TestMethod]
    public async Task ExportNeverFollowsInternalConfigLinksOrPublishesPartialArchive()
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
        var secret = Path.Combine(root, "other"); Directory.CreateDirectory(secret); await System.IO.File.WriteAllTextAsync(Path.Combine(secret, "private.txt"), "private fixture bytes");
        Directory.CreateSymbolicLink(Path.Combine(game, "config"), secret);
        await Assert.ThrowsAsync<IOException>(() => new ModrinthPackArchive().ExportAsync(new("instance", "Fixture", "1.21.1", game), new("Fixture", OverridePaths: ["config"]), Path.Combine(root, "exports")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "exports")));
    }

    [TestMethod]
    public async Task ActualVerifiedDownloaderSidecarsNeverBecomeInstalledPackFiles()
    {
        var root = TestFiles.Root(); var archive = Archive(root, Index()); var engine = new ModrinthPackArchive(); var inspection = await engine.InspectAsync(archive);
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) })));
        var downloader = new VerifiedDownloader(http); var stage = Path.Combine(root, "actual-downloader");
        await engine.StageAsync(archive, inspection.ArchiveSha256, stage, [], (file, target, ct) => downloader.DownloadAsync(new(file.Downloads.Select(u => new Uri(u)).ToArray(), target, file.Sha1, Size: file.Size), cancellationToken: ct));
        CollectionAssert.AreEquivalent(new[] { ContentInstallFiles.ManifestName, "mods/required.jar" }, Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(stage, p).Replace('\\', '/')).ToArray());
        var conflicting = Index(new JsonArray(FileEntry("config/value"), FileEntry("config/value.download.lock")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => engine.InspectAsync(Archive(root, conflicting)));
    }

    [TestMethod]
    [DataRow("vanilla")]
    [DataRow("fabric")]
    [DataRow("cancel")]
    [DataRow("persist-failure")]
    public async Task ServicePublishesOnlyCompleteNewPackInstancesAndRollsBackFailure(string scenario)
    {
        var root = TestFiles.Root(); var archive = Archive(root, Index(loader: scenario == "fabric" ? "fabric-loader" : null));
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("""{"id":"fabric-loader-0.16.5-1.21.1","inheritsFrom":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.5"}]}"""))));
        using var cancellation = new CancellationTokenSource(); var downloader = new FixtureDownloader(() => { if (scenario == "cancel") cancellation.Cancel(); });
        var platform = PlatformServicesFactory.Create(new(Path.Combine(root, "data")), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        try
        {
            var inspection = await service.InspectModrinthPackAsync(archive); var request = new ModrinthPackImportRequest(archive, inspection.ArchiveSha256, "Imported fixture");
            if (scenario == "persist-failure") Directory.CreateDirectory(Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json"));
            if (scenario == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => service.ImportModrinthPackAsync(request, cancellation.Token));
            else if (scenario == "persist-failure") await Assert.ThrowsAsync<IOException>(() => service.ImportModrinthPackAsync(request));
            else
            {
                var result = await service.ImportModrinthPackAsync(request);
                Assert.HasCount(1, service.Snapshot.Instances); Assert.IsFalse(result.Instance.Installed); Assert.AreEqual("1.21.1", result.Instance.GameVersionId);
                Assert.AreEqual(scenario, result.Instance.Loader); CollectionAssert.AreEqual(Bytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(result.Instance.GameDirectory, "mods/required.jar")));
                Assert.AreEqual(result.Instance.Id, (await ContentInstallFiles.ReadManifestAsync(result.Instance)).InstanceId);
                await using var store = new ConfigurationStore(platform.Paths); Assert.HasCount(1, (await store.LoadAsync()).Instances);
                return;
            }
            Assert.IsEmpty(service.Snapshot.Instances); Assert.IsEmpty(Directory.GetDirectories(platform.Paths.InstancesDirectory));
        }
        finally { await ((ProcessRunner)platform.Processes).DisposeAsync(); }
    }
    private sealed class FixtureDownloader(Action complete) : IVerifiedDownloader
    {
        public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        { await System.IO.File.WriteAllBytesAsync(request.DestinationPath, Bytes, cancellationToken); complete(); progress?.Report(new(request.DestinationPath, "Completed", Bytes.Length, request.Size, "fixture")); }
    }
    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
