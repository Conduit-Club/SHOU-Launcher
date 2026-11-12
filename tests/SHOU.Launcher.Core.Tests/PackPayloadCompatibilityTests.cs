using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class PackPayloadCompatibilityTests
{
    private static readonly string[] Payload = ["custom-mod-data/state.bin", "resources/ui/logo.png", "structures/base.rcst",
        "saves/preset/level.dat", "config/account-token-mod.json", "scripts/setup.sh", "scripts/native.dll", "scripts/linux.so",
        "arbitrary/script.js", ".mod-data/settings.json", "README.md", ".shou-local-input.zip", ".shou-pack-input.mrpack"];
    private static readonly byte[] Remote = [2, 3, 5, 7];
    private static JsonObject Cf(string overrides = "overrides") => new()
    {
        ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1, ["name"] = "Compatibility fixture", ["version"] = "1",
        ["minecraft"] = new JsonObject { ["version"] = "1.21.1", ["modLoaders"] = new JsonArray() }, ["files"] = new JsonArray(), ["overrides"] = overrides
    };
    private static JsonObject Mr(bool remote = false) => new()
    {
        ["formatVersion"] = 1, ["game"] = "minecraft", ["name"] = "Compatibility fixture", ["versionId"] = "1",
        ["dependencies"] = new JsonObject { ["minecraft"] = "1.21.1" },
        ["files"] = remote ? new JsonArray(new JsonObject {
            ["path"] = "custom-mod-data/download.bin", ["fileSize"] = Remote.Length,
            ["hashes"] = new JsonObject { ["sha1"] = Convert.ToHexStringLower(SHA1.HashData(Remote)), ["sha512"] = Convert.ToHexStringLower(SHA512.HashData(Remote)) },
            ["downloads"] = new JsonArray("https://pack-author.invalid:8443/file.bin?version=1") }) : new JsonArray()
    };
    private static string Zip(string marker, JsonObject metadata, string prefix, params (string Name, string Text)[] payload)
    {
        var path = Path.Combine(TestFiles.Root(), "fixture.zip"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(prefix + marker).Open(), new UTF8Encoding(false))) writer.Write(metadata.ToJsonString());
        foreach (var (name, text) in payload) { using var writer = new StreamWriter(zip.CreateEntry(prefix + name).Open()); writer.Write(text); }
        return path;
    }
    private static async Task BindManifest(string game, string id)
    {
        var path = Path.Combine(game, ContentInstallFiles.ManifestName); var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await File.ReadAllBytesAsync(path))!;
        await File.WriteAllBytesAsync(path, ContentInstallFiles.SerializeManifest(manifest with { InstanceId = id }));
    }

    [TestMethod, DataRow("overrides", ""), DataRow("overrides/", "wrapped/"), DataRow(".", ""), DataRow("./", "wrapped/")]
    public async Task CurseForgeCopiesAllDeclaredPayloadIncludingCustomDirectoriesAndScriptsAsData(string folder, string prefix)
    {
        var rootOverride = folder is "." or "./"; var source = Zip("manifest.json", Cf(folder), prefix,
            Payload.Select(p => ((rootOverride ? "" : "overrides/") + p, "payload:" + p)).ToArray());
        var archive = new LocalPackArchive(); var read = await archive.ReadAsync(source);
        Assert.IsTrue(read.CanResolveCurseForge); Assert.AreEqual(0, read.Inspection.SkippedFiles);
        Assert.AreEqual(Payload.Length + (rootOverride ? 1 : 0), read.OverrideEntries.Count);
        var stage = Path.Combine(TestFiles.Root(), "stage");
        await archive.StageCurseForgeAsync(source, read.Inspection.ArchiveSha256, stage, [], (_, _, _) => throw new AssertFailedException("No remote files."));
        foreach (var p in Payload) Assert.AreEqual("payload:" + p, await File.ReadAllTextAsync(Path.Combine(stage, p)));
        Assert.HasCount(read.OverrideEntries.Count, (await ContentInstallFiles.ReadManifestAsync(new("unregistered", "fixture", "1.21.1", stage))).Files);
    }

    [TestMethod, DataRow(""), DataRow("wrapped/")]
    public async Task MrpackKeepsCustomPayloadAppliesClientLayerAndIgnoresExtraArchiveDocumentation(string prefix)
    {
        var entries = Payload.Select(p => ("overrides/" + p, "payload:" + p)).Concat(new[] {
            ("client-overrides/resources/ui/logo.png", "client logo"), ("README.txt", "archive documentation"),
            ("screenshots/preview.png", "archive preview"), ("server-overrides/server.properties", "server-only") }).ToArray();
        var source = Zip("modrinth.index.json", Mr(true), prefix, entries); var engine = new ModrinthPackArchive();
        var inspection = await engine.InspectAsync(source); Assert.IsTrue(inspection.CanImport);
        Assert.AreEqual(LocalPackFormat.Modrinth, (await new LocalPackArchive().ReadAsync(source)).Inspection.Format);
        var game = Path.Combine(TestFiles.Root(), "game");
        using var http = new HttpClient(new ScriptedHttpHandler(request => {
            Assert.AreEqual("pack-author.invalid", request.RequestUri!.Host); Assert.AreEqual(8443, request.RequestUri.Port);
            Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(request.Headers.Contains("x-api-key"));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Remote) }); }));
        var downloader = new VerifiedDownloader(http);
        await engine.StageAsync(source, inspection.ArchiveSha256, game, [], (f, target, ct) =>
            downloader.DownloadAsync(new(f.Downloads.Select(u => new Uri(u)).ToArray(), target, f.Sha1, Size: f.Size), cancellationToken: ct));
        foreach (var p in Payload) Assert.AreEqual(p == "resources/ui/logo.png" ? "client logo" : "payload:" + p, await File.ReadAllTextAsync(Path.Combine(game, p)));
        Assert.IsFalse(File.Exists(Path.Combine(game, "README.txt"))); Assert.IsFalse(File.Exists(Path.Combine(game, "server.properties")));
        await BindManifest(game, "fixture"); var instance = new GameInstance("fixture", "fixture", "1.21.1", game);
        Assert.HasCount(Payload.Length + 1, (await ContentInstallFiles.ReadManifestAsync(instance)).Files);
        var exported = await engine.ExportAsync(instance, new("fixture", BundleInstalledFiles: true), Path.Combine(TestFiles.Root(), "export"));
        var after = await engine.InspectAsync(exported); var again = Path.Combine(TestFiles.Root(), "again");
        await engine.StageAsync(exported, after.ArchiveSha256, again, [], (_, _, _) => throw new AssertFailedException("Bundled export stays offline."));
        CollectionAssert.AreEqual(Remote, await File.ReadAllBytesAsync(Path.Combine(again, "custom-mod-data/download.bin")));
    }

    [TestMethod]
    public async Task ImportedCustomPayloadAlsoSurvivesShouAndCurseForgeExportWithoutKeywordFiltering()
    {
        var source = Zip("manifest.json", Cf("."), "", Payload.Where(p => !p.StartsWith("saves/", StringComparison.Ordinal)).Select(p => (p, "payload:" + p)).ToArray());
        var archive = new LocalPackArchive(); var read = await archive.ReadAsync(source); var game = Path.Combine(TestFiles.Root(), "game");
        await archive.StageCurseForgeAsync(source, read.Inspection.ArchiveSha256, game, [], (_, _, _) => throw new AssertFailedException());
        await BindManifest(game, "fixture"); var instance = new GameInstance("fixture", "fixture", "1.21.1", game);
        var shou = await ShouPackArchive.ExportAsync(instance, Path.Combine(TestFiles.Root(), "shou"), false, default);
        var shouRead = await archive.ReadAsync(shou); var stage = Path.Combine(TestFiles.Root(), "shou-stage");
        await ShouPackArchive.StageAsync(shou, shouRead.Inspection.ArchiveSha256, stage, default);
        foreach (var p in Payload.Where(p => !p.StartsWith("saves/", StringComparison.Ordinal))) Assert.AreEqual("payload:" + p, await File.ReadAllTextAsync(Path.Combine(stage, p)));
        var cfPlan = await CurseForgePackExport.PlanAsync(instance, new("fixture", BundleInstalledFiles: true), default);
        CollectionAssert.AreEquivalent(read.OverrideEntries.Keys.ToArray(), cfPlan.Paths.ToArray());
    }

    [TestMethod, DataRow("cf"), DataRow("mr")]
    public async Task LauncherOwnedManifestCollisionIsRejectedInsteadOfSilentlyDiscardingPackFiles(string format)
    {
        var path = Zip(format == "cf" ? "manifest.json" : "modrinth.index.json", format == "cf" ? Cf() : Mr(), "", ("overrides/" + ContentInstallFiles.ManifestName, "collision"));
        if (format == "cf") await Assert.ThrowsAsync<InvalidDataException>(() => new LocalPackArchive().ReadAsync(path));
        else await Assert.ThrowsAsync<InvalidDataException>(() => new ModrinthPackArchive().InspectAsync(path));
    }

    [TestMethod, DataRow("file:///etc/passwd"), DataRow("https://user:pass@example.invalid/file"), DataRow("https://example.invalid/file#fragment")]
    public async Task NonNetworkOrCredentialBearingSourcesRemainRejected(string url)
    {
        var index = Mr(true); index["files"]![0]!["downloads"] = new JsonArray(url);
        await Assert.ThrowsAsync<NotSupportedException>(() => new ModrinthPackArchive().InspectAsync(Zip("modrinth.index.json", index, "")));
    }

    [TestMethod]
    public async Task CurseForgeManifestAndResolverAcceptMoreThan512References()
    {
        var metadata = Cf(); metadata["files"] = new JsonArray(Enumerable.Range(1, 600).Select(i => (JsonNode)new JsonObject { ["projectID"] = i, ["fileID"] = i, ["required"] = true }).ToArray());
        var read = await new LocalPackArchive().ReadAsync(Zip("manifest.json", metadata, "")); Assert.HasCount(600, read.CurseForgeFiles);
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No API key, no network.")));
        var result = await new CurseForgePackResolver(http, null).ResolveAsync(read.CurseForgeFiles, "1.21.1", "vanilla");
        Assert.HasCount(600, result.BlockedFiles); StringAssert.Contains(result.BlockedFiles[0], "API key");
    }
}
