using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class ModDownloadSourcesTest
{
    [TestMethod]
    [DataRow("https://api.curseforge.com/v1/mods/search?gameId=432", "https://mod.mcimirror.top/curseforge/v1/mods/search?gameId=432")]
    [DataRow("https://edge.forgecdn.net/files/12/34/test.jar", "https://mod.mcimirror.top/files/12/34/test.jar")]
    [DataRow("https://api.modrinth.com/v2/search?query=iris", "https://mod.mcimirror.top/modrinth/v2/search?query=iris")]
    public void OfficialFirstAndLegacyMirrorPreferenceRemainDistinct(string official, string mirror)
    {
        CollectionAssert.AreEqual(new[] { official, mirror }, ModDownloadSources.Resolve(official, DownloadSources.Defaults()).ToArray());
        CollectionAssert.AreEqual(new[] { official, mirror }, ModDownloadSources.Resolve(official, null, legacySolution: 1).ToArray());
        CollectionAssert.AreEqual(new[] { mirror, official }, ModDownloadSources.Resolve(official, null, legacySolution: 0).ToArray());
    }

    [TestMethod]
    public void CustomOrderAndDisabledSourcesApplyToBatchPostPolicy()
    {
        const string official = "https://api.curseforge.com/v1/mods/files";
        var sources = DownloadSources.Defaults();
        sources.Single(s => s.Name == "MCIM · CurseForge API").Priority = -10;
        var order = ModDownloadSources.Resolve(official, sources);
        Assert.IsTrue(order[0].StartsWith("https://mod.mcimirror.top/curseforge/"));
        sources.Single(s => s.Category == SourceCategory.Mod && s.Layout == SourceLayout.Official).Enabled = false;
        Assert.HasCount(1, ModDownloadSources.Resolve(official, sources));
        Assert.IsEmpty(ModDownloadSources.Resolve(official, sources, allowMirror: false));
    }

    [TestMethod]
    public void OfficialOnlyRequestsAndUnknownUrlsNeverGainMirrorsOrDuplicates()
    {
        const string official = "https://api.curseforge.com/v1/fingerprints";
        CollectionAssert.AreEqual(new[] { official }, ModDownloadSources.Resolve(official, null, allowMirror: false).ToArray());
        const string unknown = "https://example.test/a";
        CollectionAssert.AreEqual(new[] { unknown }, ModDownloadSources.Resolve(unknown, null).ToArray());
        const string lookalike = "https://api.curseforge.com.evil.test/v1/mods";
        CollectionAssert.AreEqual(new[] { lookalike }, ModDownloadSources.Resolve(lookalike, null).ToArray());
    }

    [TestMethod]
    public void CachedCdnUrlsUseOfficialThenMirror()
    {
        var order = ModDownloadSources.Resolve("https://mediafilez.forgecdn.net/files/12/34/a.jar", DownloadSources.Defaults());
        CollectionAssert.AreEqual(new[] { "https://edge.forgecdn.net/files/12/34/a.jar", "https://mod.mcimirror.top/files/12/34/a.jar" }, order.ToArray());
    }

    [TestMethod]
    [DataRow("https://api.curseforge.com/v1/mods", true)]
    [DataRow("https://edge.forgecdn.net/files/12/34/a.jar", true)]
    [DataRow("https://mediafilez.forgecdn.net/files/12/34/a.jar", true)]
    [DataRow("https://mod.mcimirror.top/curseforge/v1/mods", false)]
    [DataRow("https://api.curseforge.com.evil.test/v1/mods", false)]
    [DataRow("https://api.curseforge.com:444/v1/mods", false)]
    [DataRow("https://user@api.curseforge.com/v1/mods", false)]
    [DataRow("http://api.curseforge.com/v1/mods", false)]
    public void CredentialsStayOnOfficialHttpsHosts(string url, bool expected)
        => Assert.AreEqual(expected, CurseForgeAccess.RequiresApiKey(url));
}
