using System.Net;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    [TestMethod]
    [DataRow("exact", true)]
    [DataRow("hash", false)]
    [DataRow("size", false)]
    [DataRow("version", false)]
    [DataRow("loader", false)]
    [DataRow("private", false)]
    [DataRow("folder", false)]
    [DataRow("host", false)]
    [DataRow("unavailable", false)]
    [DataRow("archived", false)]
    public async Task FtbMirrorRequiresExactPublicVersionAndFileIdentity(string scenario, bool expected)
    {
        var asset = Mod(true) with { Available = scenario is not ("unavailable" or "archived"), FileStatus = scenario == "archived" ? 8 : 4 };
        using var handler = new FixtureApi(asset); using var http = new HttpClient(handler);
        var url = "https://files.feed-the-beast.com/blob/fixture.jar";
        handler.Override = request =>
        {
            if (request.RequestUri!.Host == "api.modrinth.com") return new(HttpStatusCode.NotFound);
            if (request.RequestUri.AbsoluteUri == url) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(asset.Bytes) };
            if (request.RequestUri.Host != "api.feed-the-beast.com") return null;
            Assert.IsTrue(request.Headers.UserAgent.ToString().Contains("SHOU-Launcher", StringComparison.Ordinal));
            var route = request.RequestUri.PathAndQuery;
            if (route.Contains("/search/", StringComparison.Ordinal)) return ScriptedHttpHandler.Json("{\"packs\":[130]}");
            if (route.EndsWith("/130", StringComparison.Ordinal)) return ScriptedHttpHandler.Json("{\"id\":130,\"private\":false,\"name\":\"FTB Fixture\",\"versions\":[{\"id\":2,\"private\":false,\"name\":\"1.0.0\"}]}");
            var file = new JsonObject
            {
                ["curseforge"] = new JsonObject { ["project"] = asset.Project.ToString(), ["file"] = asset.FileId.ToString() },
                ["path"] = scenario == "folder" ? "./resourcepacks" : "./mods",
                ["sha1"] = scenario == "hash" ? new string('0', 40) : asset.Sha1,
                ["hashes"] = new JsonObject { ["sha1"] = asset.Sha1, ["sha512"] = asset.Sha512 },
                ["size"] = asset.Bytes.Length + (scenario == "size" ? 1 : 0), ["url"] = scenario == "host" ? "https://unknown.invalid/file.jar" : url
            };
            return ScriptedHttpHandler.Json(new JsonObject
            {
                ["parent"] = 130, ["id"] = 2, ["name"] = scenario == "version" ? "2.0.0" : "1.0.0", ["private"] = scenario == "private",
                ["targets"] = new JsonArray(new JsonObject { ["name"] = "minecraft", ["version"] = "1.21.1" }, new JsonObject { ["name"] = "fabric", ["version"] = scenario == "loader" ? "0.1.0" : "0.16.14" }),
                ["files"] = new JsonArray(file)
            }.ToJsonString());
        };
        var resolver = new CurseForgePackResolver(http, FixtureKey);
        var result = await resolver.ResolveAsync([new(asset.Project, asset.FileId, true)], "1.21.1", "fabric", ftbPack: new("FTB Fixture", "1.0.0", "1.21.1", "fabric", "0.16.14"));
        if (!expected) { Assert.IsEmpty(result.Files); Assert.HasCount(1, result.BlockedFiles); return; }
        var resolved = result.Files.Single(); Assert.IsTrue(resolved.FromFtb); Assert.IsFalse(resolved.FromModrinth);
        var path = Path.Combine(TestFiles.Root(), "ftb.jar"); await resolver.DownloadAsync(resolved, path);
        CollectionAssert.AreEqual(asset.Bytes, await File.ReadAllBytesAsync(path));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.DownloadAsync(resolved with { Sha512 = new string('0', 128) }, path));
        Assert.IsFalse(handler.Requests.Any(r => r.Host == "unknown.invalid"));
    }
    [TestMethod]
    public async Task CurseForgePackExposesVersionAndRecommendedMemory()
    {
        var manifest = Manifest(Mod()); manifest["minecraft"]!["recommendedRam"] = 8192;
        var read = await new LocalPackArchive().ReadAsync(Archive(manifest));
        Assert.AreEqual("1.0.0", read.Inspection.PackVersion); Assert.AreEqual(8192, read.Inspection.RecommendedMemoryMiB);
    }
}
