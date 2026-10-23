using System;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
[TestCategory("Integration")]
public sealed class DownloadSourcesIntegrationTest
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void LivePreferredManifestKeepsTheFullVersionList()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var result = DownloadSources.FindManifest(
            DownloadSources.Resolve([DownloadSources.ManifestUrl], DownloadSources.Defaults()),
            url => client.GetStringAsync(url).GetAwaiter().GetResult(),
            onFailure: (source, ex) => TestContext.WriteLine($"{source.Name}: {ex.Message}"));
        TestContext.WriteLine($"Source: {result.Source.Name}; versions: {result.Manifest["versions"]!.AsArray().Count}");
        Assert.IsFalse(result.Source.IsPartialCatalog, "This integration requires at least one healthy full-catalog endpoint.");
        Assert.IsGreaterThan(100, result.Manifest["versions"]!.AsArray().Count);
    }
}
