using System.Net;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfParityCatalogTests
{
    private static IReadOnlyList<DownloadSource> Official() => [new() { Category = SourceCategory.Minecraft, Layout = SourceLayout.Official }, new() { Category = SourceCategory.Loader, Layout = SourceLayout.Official }, new() { Category = SourceCategory.Mod, Layout = SourceLayout.Official }];
    [TestMethod]
    public async Task VersionCatalogUsesActualReleaseTypesAndLatestMarkers()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"latest":{"release":"1.21.1"},"versions":[{"id":"1.21.1","type":"release","releaseTime":"2024-08-08T12:00:00Z"},{"id":"snapshot-fixture","type":"snapshot","releaseTime":"2024-08-01T12:00:00Z"},{"id":"3D Shareware v1.34","type":"snapshot","releaseTime":"2019-04-01T12:00:00Z"}]}""") })));
        var result = await new LauncherCatalog(http, Official).GamesAsync();
        Assert.AreEqual(3, result.Versions.Count);
        Assert.IsTrue(result.Versions[0].Latest); Assert.IsFalse(result.Versions[1].Latest);
        Assert.AreEqual("snapshot", result.Versions[1].Kind); Assert.IsFalse(result.Partial);
        Assert.AreEqual("3D Shareware v1.34", result.Versions[2].Id); Assert.IsFalse(result.Versions[2].CanInstall);
    }
    [TestMethod]
    public async Task ContentFiltersAreEncodedAsFacetsAndNotExecutableText()
    {
        Uri? observed = null;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            observed = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"hits":[{"project_id":"fixture","slug":"fixture-slug","title":"Dynamic title","description":"observed","author":"author","downloads":42}]}""") });
        }));
        var result = await new LauncherCatalog(http, Official).SearchAsync(new("mod", "中文 & loader=fake", "1.21.1", "fabric"));
        Assert.AreEqual("Dynamic title", result[0].Title);
        Assert.IsTrue(observed!.AbsoluteUri.Contains("query=%E4%B8%AD%E6%96%87%20%26%20loader%3Dfake", StringComparison.Ordinal));
        Assert.IsTrue(Uri.UnescapeDataString(observed.Query).Contains("categories:fabric", StringComparison.Ordinal));
        Assert.AreEqual("https://modrinth.com/mod/fixture-slug", result[0].PageUri.AbsoluteUri);
    }
    [TestMethod]
    public async Task UntrustedFileUrlsAndTraversalNamesNeverBecomeDownloadActions()
    {
        string FileJson(string name, string url) => new JsonArray(new JsonObject
        {
            ["id"] = "release1", ["files"] = new JsonArray(new JsonObject { ["filename"] = name, ["url"] = url,
                ["size"] = 8, ["hashes"] = new JsonObject { ["sha1"] = new string('a', 40) } })
        }).ToJsonString();
        foreach (var (name, url) in new[] { ("../escape.jar", "https://cdn.modrinth.com/file.jar"), ("safe.jar", "https://cdn.modrinth.com.evil.invalid/file.jar") })
        {
            using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FileJson(name, url)) })));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new LauncherCatalog(http, Official).ReleasesAsync("fixture", null, null));
        }
    }
}
