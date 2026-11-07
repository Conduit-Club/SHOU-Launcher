using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Loaders;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class InstallerCatalogTests
{
    [TestMethod]
    public async Task HistoricalFabricBuildCoordinatesCannotInvalidateTheModernLoaderDirectory()
    {
        var malformed = false;
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json(malformed
            ? """[{"loader":{"version":"../escape","stable":true}}]"""
            : """[{"loader":{"version":"0.20.1","stable":true}},{"loader":{"version":"0.10.6+build.214","stable":false}},{"loader":{"version":"0.19.3","stable":true}}]"""))));
        var catalog = new LauncherCatalog(http, () => [new() { Category = SourceCategory.Loader, Layout = SourceLayout.Official }]);
        var versions = await catalog.FabricAsync("26.3");
        CollectionAssert.AreEqual(new[] { "0.20.1", "0.19.3" }, versions.Select(v => v.Version).ToArray());
        malformed = true;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.FabricAsync("26.3"));
    }
    [TestMethod]
    public async Task ForgeDirectoryGroupsGameIdentityAndUsesActualRecommendations()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(ScriptedHttpHandler.Json(request.RequestUri!.AbsolutePath.EndsWith(".json")
            ? """{"promos":{"26.3-recommended":"66.1.0"}}"""
            : "<metadata><versions><version>1.21.1-52.1.9</version><version>26.3-66.1.5</version><version>26.3-66.1.0</version></versions></metadata>"))));
        var result = await new InstallerCatalogReader(http).ReadAsync("forge", default);
        Assert.AreEqual("26.3", result.Versions[0].GameVersion); Assert.AreEqual("66.1.5", result.Versions[0].Version);
        Assert.IsTrue(result.Versions.Single(v => v.Version == "66.1.0").Recommended);
        Assert.AreEqual("/net/minecraftforge/forge/26.3-66.1.5/forge-26.3-66.1.5-installer.jar", InstallerCatalogReader.ArtifactUri(result.Versions[0]).AbsolutePath);
    }
    [TestMethod]
    public async Task FabricDirectoryIgnoresRemoteDownloadUrlAndUsesPinnedOfficialCoordinate()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("""[{"version":"1.1.2","stable":true,"url":"https://evil.invalid/private.jar"}]"""))));
        var row = (await new InstallerCatalogReader(http).ReadAsync("fabric", default)).Versions.Single();
        Assert.AreEqual("maven.fabricmc.net", InstallerCatalogReader.ArtifactUri(row).Host);
        var legacy = (await new InstallerCatalogReader(http).ReadAsync("legacyfabric", default)).Versions.Single();
        Assert.AreEqual("repo.legacyfabric.net", InstallerCatalogReader.ArtifactUri(legacy).Host);
        Assert.AreEqual("/legacyfabric/net/legacyfabric/fabric-installer/1.1.2/fabric-installer-1.1.2.jar", InstallerCatalogReader.ArtifactUri(legacy).AbsolutePath);
        Assert.ThrowsExactly<ArgumentException>(() => InstallerCatalogReader.ArtifactUri(row with { ArtifactVersion = "../escape" }));
        Assert.ThrowsExactly<InvalidDataException>(() => InstallerCatalogReader.ArtifactUri(row with { ArtifactVersion = "other-version" }));
    }
    [TestMethod]
    public async Task LegacyBuildMetadataIsEscapedAsOnePinnedOfficialCoordinate()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("""[{"version":"1.1.0+1","stable":true,"url":"https://evil.invalid/private.jar"}]"""))));
        var row = (await new InstallerCatalogReader(http).ReadAsync("legacyfabric", default)).Versions.Single();
        var uri = InstallerCatalogReader.ArtifactUri(row);
        Assert.AreEqual("https://repo.legacyfabric.net/legacyfabric/net/legacyfabric/fabric-installer/1.1.0%2B1/fabric-installer-1.1.0%2B1.jar", uri.AbsoluteUri);
        foreach (var invalid in new[] { "../escape", "%2e%2e", "1.0?private", "https:evil" })
            Assert.ThrowsExactly<ArgumentException>(() => InstallerCatalogReader.ArtifactUri(row with { Version = invalid, ArtifactVersion = invalid }));
    }
    [TestMethod]
    public async Task NeoForgeDirectoryKeepsCalendarHotfixesSeparateAndSkipsUnmappedAlphaCoordinates()
    {
        const string metadata = "<metadata><versions><version>21.1.265</version><version>26.1.0.19-beta</version><version>26.1.1.15-beta</version><version>26.1.2.126</version><version>26.1.0.0-alpha.4+snapshot-1</version></versions></metadata>";
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json(metadata))));
        var rows = (await new InstallerCatalogReader(http).ReadAsync("neoforge", default)).Versions;
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual("26.1.2", rows.Single(r => r.Version == "26.1.2.126").GameVersion);
        Assert.AreEqual("26.1", rows.Single(r => r.Version == "26.1.0.19-beta").GameVersion);
        var loader = new LoaderCatalog(http);
        Assert.AreEqual("26.1.2.126", (await loader.GetAsync("neoforge", "26.1.2", default)).Single().Version);
        Assert.AreEqual("26.1.0.19-beta", (await loader.GetAsync("neoforge", "26.1", default)).Single().Version);
        Assert.AreEqual("26.1.2.", LoaderCatalog.NeoPrefix("26.1.2"));
        Assert.AreEqual("26.1.0.", LoaderCatalog.NeoPrefix("26.1"));
    }
    [TestMethod]
    public async Task MetadataRejectsDtdAndUnknownProvidersHaveAnExplicitEmptyResult()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("<!DOCTYPE metadata><metadata/>"))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new InstallerCatalogReader(http).ReadAsync("forge", default));
        var unavailable = await new InstallerCatalogReader(http).ReadAsync("optifine", default);
        Assert.AreEqual(0, unavailable.Versions.Count); Assert.IsFalse(string.IsNullOrWhiteSpace(unavailable.Notice));
    }
    [TestMethod]
    public async Task SaveVerifiesHashPublishesOnlyCompleteFileAndKeepsStagingOutOfDestination()
    {
        var bytes = Encoding.UTF8.GetBytes("controlled installer fixture, never executed");
        foreach (var provider in new[] { "fabric", "legacyfabric" })
        {
            using var http = InstallerTransport(bytes, Convert.ToHexStringLower(SHA1.HashData(bytes)), provider == "fabric" ? "maven.fabricmc.net" : "repo.legacyfabric.net");
            var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r12", Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "中文 output", "fabric installer.jar"); var staging = Path.Combine(root, "staging");
            var reader = new InstallerCatalogReader(http); var row = new InstallerVersion(provider, "通用安装器", "1.1.2", "1.1.2");
            await reader.SaveAsync(row, target, staging, null, default);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
            CollectionAssert.AreEqual(new[] { "fabric installer.jar" }, Directory.GetFiles(Path.GetDirectoryName(target)!).Select(Path.GetFileName).ToArray());
            Assert.AreEqual(0, Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Length);
            await reader.SaveAsync(row, target, staging, null, default); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
        }
    }
    [TestMethod]
    public async Task CorruptTransferPreservesExistingDestinationAndCleansOwnedStaging()
    {
        var bytes = Encoding.UTF8.GetBytes("controlled wrong bytes"); using var http = InstallerTransport(bytes, new string('a', 40));
        var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var target = Path.Combine(root, "keep.jar"); var staging = Path.Combine(root, "staging");
        await File.WriteAllTextAsync(target, "existing user file");
        await Assert.ThrowsExactlyAsync<AggregateException>(() => new InstallerCatalogReader(http).SaveAsync(new("fabric", "通用安装器", "1.1.2", "1.1.2"), target, staging, null, default));
        Assert.AreEqual("existing user file", await File.ReadAllTextAsync(target)); Assert.AreEqual(0, Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Length);
    }
    [TestMethod]
    public async Task CancellationKeepsExistingFileAndRemovesOnlyOwnedTemporaryFiles()
    {
        var bytes = Encoding.UTF8.GetBytes("controlled cancelled fixture"); using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha1")) return Task.FromResult(ScriptedHttpHandler.Json(Convert.ToHexStringLower(SHA1.HashData(bytes))));
            if (request.Method == HttpMethod.Get) cancel.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var target = Path.Combine(root, "keep.jar"); var staging = Path.Combine(root, "staging");
        await File.WriteAllTextAsync(target, "existing user file");
        try { await new InstallerCatalogReader(http).SaveAsync(new("fabric", "通用安装器", "1.1.2", "1.1.2"), target, staging, null, cancel.Token); Assert.Fail("A cancelled transfer must not publish."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual("existing user file", await File.ReadAllTextAsync(target));
        if (Directory.Exists(staging)) Assert.AreEqual(0, Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Length);
    }
    [TestMethod]
    public async Task ContentTagsAndMetadataReachThePortableContract()
    {
        Uri? observed = null;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            observed = request.RequestUri;
            return Task.FromResult(ScriptedHttpHandler.Json("""{"hits":[{"project_id":"fixture","slug":"fixture","title":"Fixture","downloads":283201701,"categories":["fabric","library"],"versions":["1.21.1"],"date_modified":"2026-01-02T03:04:05Z"}]}"""));
        }));
        var catalog = new LauncherCatalog(http, () => [new() { Category = SourceCategory.Mod, Layout = SourceLayout.Official }]);
        var rows = await catalog.SearchAsync(new("mod", Sort: "downloads", Category: "library"));
        Assert.IsTrue(Uri.UnescapeDataString(observed!.Query).Contains("categories:library", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "fabric", "library" }, rows[0].Categories!.ToArray());
        Assert.IsNotNull(rows[0].Updated); Assert.AreEqual(283201701L, rows[0].Downloads);
        await catalog.SearchAsync(new("datapack", Sort: "downloads"));
        Assert.IsTrue(Uri.UnescapeDataString(observed!.Query).Contains("project_type:datapack", StringComparison.Ordinal));
        Assert.IsFalse(Uri.UnescapeDataString(observed.Query).Contains("categories:datapack", StringComparison.Ordinal));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.SearchAsync(new("mod", Category: "invalid:facet")));
    }
    private static HttpClient InstallerTransport(byte[] bytes, string sha, string host = "maven.fabricmc.net") => new(new ScriptedHttpHandler(request =>
    {
        Assert.AreEqual(host, request.RequestUri!.Host); Assert.IsNull(request.Headers.Authorization);
        return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith(".sha1") ? ScriptedHttpHandler.Json(sha)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }));
}
