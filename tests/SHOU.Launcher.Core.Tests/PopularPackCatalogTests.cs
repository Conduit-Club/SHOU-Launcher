using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Catalog;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class PopularPackCatalogTests
{
    [TestMethod]
    public async Task HistoricalVersionWithoutSha1DoesNotHideValidLatestPack()
    {
        JsonObject File(int id, bool hash) => new()
        {
            ["id"] = id, ["modId"] = 925200, ["gameId"] = 432, ["fileName"] = $"ATM10-{id}.zip", ["fileLength"] = 205881742,
            ["displayName"] = $"ATM10 {id}", ["downloadUrl"] = $"https://edge.forgecdn.net/files/1/2/ATM10-{id}.zip", ["isAvailable"] = true,
            ["hashes"] = hash ? new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = new string('a', 40) }) : new JsonArray(), ["gameVersions"] = new JsonArray("1.21.1", "NeoForge")
        };
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json(new JsonObject { ["data"] = new JsonArray(File(9431424, true), File(6273343, false)) }.ToJsonString()))));
        var releases = await new CurseForgeCatalog(http, "fixture-only").ReleasesAsync("cf-925200", null, null, default);
        Assert.HasCount(2, releases); Assert.HasCount(1, releases[0].Files); Assert.HasCount(0, releases[1].Files);
        StringAssert.Contains(releases[1].Notice!, "SHA-1");
    }
}
