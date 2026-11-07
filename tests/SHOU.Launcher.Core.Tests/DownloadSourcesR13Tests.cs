using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Loaders;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class DownloadSourcesR13Tests
{
    [TestMethod]
    public void AprilGroupingKeepsNormalAprilReleasesAndOrdinarySnapshotsSeparate()
    {
        foreach (var id in new[] { "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a" })
            Assert.AreEqual("愚人节版", MinecraftCatalogGroups.Group(new(id, "snapshot", DateTimeOffset.UnixEpoch)));
        Assert.AreEqual("正式版", MinecraftCatalogGroups.Group(new("26.1.1", "release", new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero))));
        Assert.AreEqual("预览版", MinecraftCatalogGroups.Group(new("24w14a", "snapshot", DateTimeOffset.UnixEpoch)));
    }
    private const string WorldProject = """{"id":307740,"gameId":432,"classId":17,"name":"World fixture","slug":"world-fixture","downloadCount":42,"links":{"websiteUrl":"https://www.curseforge.com/minecraft/worlds/world-fixture"},"categories":[{"name":"Adventure"}],"authors":[{"name":"Fixture"}],"latestFilesIndexes":[{"gameVersion":"1.21.1"}]}""";
    [TestMethod]
    public async Task CurseForgeWorldSearchUsesItsRealClassAndPreservesProviderIdentity()
    {
        Uri? observed = null;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            observed = request.RequestUri;
            Assert.AreEqual("api.curseforge.com", observed!.Host); Assert.AreEqual("fixture-only", request.Headers.GetValues("x-api-key").Single());
            return Task.FromResult(ScriptedHttpHandler.Json("{\"data\":[" + WorldProject + "]}"));
        }));
        var result = await new CurseForgeCatalog(http, "fixture-only").SearchAsync(new("world", "空 格&", "1.21.1", "fabric", Source: "curseforge"), default);
        Assert.IsTrue(observed!.Query.Contains("classId=17")); Assert.IsTrue(observed.Query.Contains("searchFilter=%E7%A9%BA%20%E6%A0%BC%26"));
        var row = result.Single(); Assert.AreEqual("cf-307740", row.Id); Assert.AreEqual("curseforge", row.Source); Assert.AreEqual("world", row.Kind);
        Assert.IsFalse(http.DefaultRequestHeaders.Contains("x-api-key"));
    }
    [TestMethod]
    public async Task CurseForgeApiRejectsRedirectsAndAbsentKeysWithoutFollowingThem()
    {
        var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://evil.invalid/private") } }); }));
        await Assert.ThrowsExactlyAsync<IOException>(() => new CurseForgeCatalog(http, "fixture-only").SearchAsync(new("mod"), default));
        Assert.AreEqual(1, calls);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new CurseForgeCatalog(http, null).SearchAsync(new("mod"), default)); Assert.AreEqual(1, calls);
    }
    [TestMethod]
    public async Task CurseForgeFilesRequireExactIdentityCdnHashAndExplicitAuthorDownloadPermission()
    {
        var address = "https://mediafilez.forgecdn.net/files/1/2/fixture.zip"; var modId = 307740;
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json($$"""{"data":[{"id":42,"modId":{{modId}},"gameId":432,"fileName":"fixture.zip","displayName":"Fixture","downloadUrl":"{{address}}","fileLength":1234,"hashes":[{"algo":1,"value":"{{new string('a',40)}}"}],"gameVersions":["1.21.1"]}]}"""))));
        var catalog = new CurseForgeCatalog(http, "fixture-only");
        var release = (await catalog.ReleasesAsync("cf-307740", null, null, default)).Single();
        Assert.AreEqual("cf-307740-42", release.Id); Assert.AreEqual("cf-307740", release.ProjectId); Assert.AreEqual(1, release.Files.Count);
        address = ""; release = (await catalog.ReleasesAsync("cf-307740", null, null, default)).Single(); Assert.AreEqual(0, release.Files.Count); Assert.IsNotNull(release.Notice);
        address = "https://evil.invalid/fixture.zip";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReleasesAsync("cf-307740", null, null, default));
        address = "https://mediafilez.forgecdn.net/files/1/2/fixture.zip"; modId = 12;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReleasesAsync("cf-307740", null, null, default));
    }
    [TestMethod]
    public async Task FourAdditionalCatalogsExposeActualVersionAndPinnedDownloadKinds()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(ScriptedHttpHandler.Json(request.RequestUri!.Host switch
        {
            "bmclapi2.bangbang93.com" => """[{"mcversion":"1.21.1","type":"HD_U","patch":"J1","filename":"OptiFine_1.21.1_HD_U_J1.jar"},{"mcversion":"1.21.1","type":"HD_U_J1","patch":"pre9","filename":"preview_OptiFine_1.21.1_HD_U_J1_pre9.jar"},{"mcversion":"1.21.1","type":"HD_U_J1","patch":"pre10","filename":"preview_OptiFine_1.21.1_HD_U_J1_pre10.jar"}]""",
            "maven.cleanroommc.com" => "<metadata><versioning><versions><version>0.7.14-alpha</version><version>0.7.13-alpha</version></versions></versioning></metadata>",
            "releases.r2.labymod.net" => """{"labyModVersion":"48.4","commitReference":"d78cc051","releaseTime":1793703795884,"sha1":"this-is-a-client-hash-not-an-installer-hash"}""",
            "dl.liteloader.com" => """{"versions":{"1.7.10":{"artefacts":{"com.mumfrey:liteloader":{"latest":{"version":"1.7.10_04","stream":"RELEASE","timestamp":"1414368553","md5":"this-is-the-loader-not-installer"}}}}}}""",
            _ => throw new InvalidOperationException()
        }))));
        foreach (var loader in new[] { "optifine", "cleanroom", "labymod", "liteloader" })
        {
            var result = await new InstallerCatalogReader(http).ReadAsync(loader, default); Assert.IsTrue(result.Versions.Count > 0);
            var row = result.Versions[0]; Assert.AreEqual(loader, row.Loader); Assert.IsTrue(InstallerCatalogReader.InstallerFileName(row).EndsWith(".jar"));
            Assert.AreEqual("https", InstallerCatalogReader.ArtifactUri(row).Scheme);
            Assert.ThrowsExactly<ArgumentException>(() => InstallerCatalogReader.ArtifactUri(row with { ArtifactVersion = "../escape" }));
            if (loader == "optifine") { Assert.AreEqual(3, result.Versions.Count); Assert.AreEqual("J1_pre10", row.Version); Assert.AreEqual("preview", row.Channel); }
        }
    }
    private static byte[] Jar()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("META-INF/MANIFEST.MF").Open())) writer.Write("Manifest-Version: 1.0\n");
            using var payload = archive.CreateEntry("fixture.bin", CompressionLevel.NoCompression).Open(); payload.Write(new byte[2048]);
        }
        return memory.ToArray();
    }
    [TestMethod]
    public async Task OptiFinePreviewIsDownloadedFromOfficialHostAndCheckedAgainstMirrorIdentity()
    {
        var bytes = Jar(); var cdnVisited = false; var corrupt = false;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Assert.IsFalse(request.Headers.Contains("x-api-key")); var uri = request.RequestUri!;
            if (uri.Host == "bmclapi2.bangbang93.com")
            {
                if (uri.AbsolutePath.EndsWith("versionList")) return Task.FromResult(ScriptedHttpHandler.Json("""[{"mcversion":"1.21.1","type":"HD_U_J1","patch":"pre10","filename":"preview_OptiFine_1.21.1_HD_U_J1_pre10.jar"}]"""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://untrusted-cdn.invalid/download/" + Convert.ToHexStringLower(SHA1.HashData(bytes)) + "?signed=not-a-credential") } });
            }
            if (uri.Host != "optifine.net") { cdnVisited = true; throw new InvalidOperationException(); }
            if (uri.AbsolutePath == "/adloadx") return Task.FromResult(ScriptedHttpHandler.Json("<a href=\"downloadx?f=preview_OptiFine_1.21.1_HD_U_J1_pre10.jar&amp;x=public-fixture\">Download</a>"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? bytes.Concat(new byte[1]).ToArray() : bytes) });
        }));
        var reader = new InstallerCatalogReader(http); var row = (await reader.ReadAsync("optifine", default)).Versions.Single();
        var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r13", Guid.NewGuid().ToString("N")); var output = Path.Combine(root, "preview.jar");
        await reader.SaveAsync(row, output, Path.Combine(root, "stage"), null, default); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(output)); Assert.IsFalse(cdnVisited);
        corrupt = true; await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.SaveAsync(row, output, Path.Combine(root, "stage"), null, default));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(output));
    }
    [TestMethod]
    public async Task OptiFineFallbackCannotContactALocalOrLiteralIpDestination()
    {
        var contactedLocal = false;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "bmclapi2.bangbang93.com")
            {
                if (uri.AbsolutePath.EndsWith("versionList")) return Task.FromResult(ScriptedHttpHandler.Json("""[{"mcversion":"1.21.1","type":"HD_U","patch":"J1","filename":"OptiFine_1.21.1_HD_U_J1.jar"}]"""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://127.0.0.1/download/" + new string('a', 40)) } });
            }
            if (uri.Host == "optifine.net") return Task.FromResult(ScriptedHttpHandler.Json(uri.AbsolutePath == "/adloadx" ? "<a href=\"downloadx?f=OptiFine_1.21.1_HD_U_J1.jar&amp;x=fixture\">download</a>" : "unavailable"));
            contactedLocal = true; throw new InvalidOperationException();
        }));
        var reader = new InstallerCatalogReader(http); var row = (await reader.ReadAsync("optifine", default)).Versions.Single();
        var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r13", Guid.NewGuid().ToString("N")); var output = Path.Combine(root, "installer.jar");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.SaveAsync(row, output, Path.Combine(root, "stage"), null, default));
        Assert.IsFalse(contactedLocal); Assert.IsFalse(File.Exists(output));
    }
    [TestMethod]
    public async Task LabyAndHistoricalLiteSaveRealJarsWithoutMisusingClientOrLoaderHashes()
    {
        var bytes = Jar(); var html = false;
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("latest.json") => ScriptedHttpHandler.Json("""{"labyModVersion":"48.4","commitReference":"d78cc051","sha1":"client-hash-must-not-be-used"}"""),
            var p when p.EndsWith("versions.json") => ScriptedHttpHandler.Json("""{"versions":{"1.7.10":{"artefacts":{"com.mumfrey:liteloader":{"latest":{"version":"1.7.10_04","stream":"RELEASE","md5":"loader-hash-must-not-be-used"}}}}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(html ? Encoding.UTF8.GetBytes("<html>" + new string('x', 2048) + "</html>") : bytes) }
        })));
        var reader = new InstallerCatalogReader(http);
        foreach (var loader in new[] { "labymod", "liteloader" })
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r13", Guid.NewGuid().ToString("N")); var output = Path.Combine(root, "installer.jar"); var stage = Path.Combine(root, "stage");
            var row = (await reader.ReadAsync(loader, default)).Versions[0]; html = false;
            await reader.SaveAsync(row, output, stage, null, default); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(output));
            html = true; await Assert.ThrowsAsync<InvalidDataException>(() => reader.SaveAsync(row, output, stage, null, default));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(output)); Assert.AreEqual(0, Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length);
        }
    }
    [TestMethod]
    public async Task AdditionalDownloadsPreserveOldFileOnBadHashOrHtmlAndCleanTheirStaging()
    {
        var bytes = Jar(); var corrupt = false; var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r13", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "中文 output", "installer.jar"); var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllTextAsync(target, "original");
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("maven-metadata.xml") => ScriptedHttpHandler.Json("<metadata><versions><version>0.7.14-alpha</version></versions></metadata>"),
            var p when p.EndsWith(".sha1") => ScriptedHttpHandler.Json(Convert.ToHexStringLower(SHA1.HashData(bytes))),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? Encoding.UTF8.GetBytes(new string('x', 2048)) : bytes) }
        })));
        var reader = new InstallerCatalogReader(http); var row = (await reader.ReadAsync("cleanroom", default)).Versions.Single();
        await reader.SaveAsync(row, target, stage, null, default); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
        corrupt = true; await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.SaveAsync(row, target, stage, null, default));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target)); Assert.AreEqual(0, Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length);
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.SaveAsync(row, target, stage, null, cancelled.Token));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
    }
}
