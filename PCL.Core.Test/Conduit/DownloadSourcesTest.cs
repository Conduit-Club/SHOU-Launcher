using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class DownloadSourcesTest
{
    private const string SmallManifest = """{"latest":{"release":"1.21.1","snapshot":"1.21.1"},"versions":[{"id":"1.21.1","url":"https://example.com/version.json"}]}""";

    [TestMethod]
    public void DefaultManifestAndAssetsFollowRequestedPriority()
    {
        var urls = DownloadSources.Resolve([DownloadSources.ManifestUrl], DownloadSources.Defaults());
        Assert.AreEqual(3, urls.Count);
        Assert.AreEqual(DownloadSources.BmclRoot + "/mc/game/version_manifest.json", urls[0].Url);
        Assert.AreEqual(DownloadSources.ShouRoot + "/metadata/version_manifest_v2.json", urls[1].Url);
        Assert.AreEqual(DownloadSources.ManifestUrl, urls[2].Url);
        var assets = DownloadSources.Resolve(["https://resources.download.minecraft.net/ab/abcdef"], DownloadSources.Defaults());
        Assert.AreEqual(DownloadSources.ShouRoot + "/upstream/resources.download.minecraft.net/ab/abcdef", assets[1].Url);
    }

    [TestMethod]
    public void RewrittenShouAndBmclUrlsRetainFallbacks()
    {
        var urls = DownloadSources.Resolve([DownloadSources.ShouRoot + "/upstream/libraries.minecraft.net/a/b.jar", DownloadSources.BmclRoot + "/maven/a/b.jar"], DownloadSources.Defaults());
        Assert.AreEqual(3, urls.Count);
        Assert.AreEqual("https://libraries.minecraft.net/a/b.jar", urls[2].Url);
        var loader = DownloadSources.Resolve([DownloadSources.BmclRoot + "/fabric-meta/v2/versions"], DownloadSources.Defaults());
        Assert.AreEqual(2, loader.Count);
        Assert.AreEqual("https://meta.fabricmc.net/v2/versions", loader[1].Url);
        var metadata = DownloadSources.Resolve([DownloadSources.ShouRoot + "/metadata/versions/rewritten-sha.json", "https://piston-meta.mojang.com/v1/packages/original-sha/version.json"], DownloadSources.Defaults());
        Assert.AreEqual(3, metadata.Count);
        Assert.IsTrue(metadata[0].Url.StartsWith(DownloadSources.BmclRoot));
        Assert.AreEqual(DownloadSources.ShouRoot + "/metadata/versions/rewritten-sha.json", metadata[1].Url);
    }

    [TestMethod]
    public void CustomPrefixSupportsPriorityBoundaryAndDisable()
    {
        var sources = DownloadSources.Defaults();
        var custom = new DownloadSource { Name = "测试", Category = SourceCategory.Loader, OriginPrefix = "https://meta.fabricmc.net/v2", BaseUrl = "https://custom.test/api", Priority = -10 };
        sources.Add(custom);
        Assert.AreEqual("https://custom.test/api/versions?x=1", DownloadSources.Resolve(["https://meta.fabricmc.net/v2/versions?x=1"], sources)[0].Url);
        Assert.IsFalse(DownloadSources.Matches("https://meta.fabricmc.net.evil/v2", "https://meta.fabricmc.net"));
        Assert.IsFalse(DownloadSources.Matches("https://meta.fabricmc.net/v20/test", "https://meta.fabricmc.net/v2"));
        custom.Enabled = false;
        Assert.AreEqual(DownloadSources.BmclRoot + "/fabric-meta/v2/versions", DownloadSources.Resolve(["https://meta.fabricmc.net/v2/versions"], sources)[0].Url);
    }

    [TestMethod]
    public void MissingVersionMalformedAndUnavailableSourcesFallThroughInOrder()
    {
        var visited = new List<string>();
        var sources = new[] { new SourceAddress("a", "a", 0), new SourceAddress("b", "b", 100), new SourceAddress("c", "c", 200), new SourceAddress("d", "d", 300) };
        var result = DownloadSources.FindManifest(sources, url =>
        {
            visited.Add(url);
            return url switch { "a" => SmallManifest.Replace("1.21.1", "1.20.1"), "b" => "<html>bad gateway</html>", "c" => SmallManifest, _ => throw new Exception("不应访问") };
        }, "1.21.1");
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, visited);
        Assert.AreEqual("c", result.Source.Name);
        Assert.AreEqual(1, result.Manifest["versions"]!.AsArray().Count);
    }

    [TestMethod]
    public void NoLoaderOrModRequestsGoToShouAndUnrelatedUrlsArePreserved()
    {
        foreach (var url in new[] { "https://meta.fabricmc.net/v2/versions", "https://cdn.modrinth.com/data/test.jar" })
            Assert.IsFalse(DownloadSources.Resolve([url], DownloadSources.Defaults()).Any(s => s.Url.StartsWith(DownloadSources.ShouRoot)));
        var unknown = "https://example.com/other?a=1";
        Assert.AreEqual(unknown, DownloadSources.Resolve([unknown], DownloadSources.Defaults()).Single().Url);
        Assert.AreEqual("https://edge.forgecdn.net/files/123/a.jar", DownloadSources.Resolve(["https://mod.mcimirror.top/files/123/a.jar"], DownloadSources.Defaults())[0].Url);
    }
}
