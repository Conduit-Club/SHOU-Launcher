using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    [TestMethod]
    public async Task AuthorBlockedFileCanBeProvidedOnlyWithExactHashAndSizeThenUsedWithoutNetwork()
    {
        var asset = Mod(true); var cache = TestFiles.Root(); using var handler = new FixtureApi(asset); using var http = new HttpClient(handler);
        handler.Override = req => req.RequestUri!.Host == "api.modrinth.com" ? new(HttpStatusCode.NotFound) : null;
        var resolver = new CurseForgePackResolver(http, FixtureKey, cache);
        var unresolved = await resolver.ResolveAsync([new(asset.Project, asset.FileId, true)], "1.21.1", "fabric");
        Assert.HasCount(1, unresolved.BlockedFiles); var manual = unresolved.ManualFiles!.Single(); Assert.AreEqual(asset.Sha1, manual.Sha1);
        var source = Path.Combine(TestFiles.Root(), asset.Name); File.WriteAllText(source, "wrong version");
        await Assert.ThrowsAsync<InvalidDataException>(() => CurseForgeManualCache.StoreAsync(cache, manual, source, default));
        await File.WriteAllBytesAsync(source, asset.Bytes); await CurseForgeManualCache.StoreAsync(cache, manual, source, default);
        var resolved = await resolver.ResolveAsync([new(asset.Project, asset.FileId, true)], "1.21.1", "fabric");
        Assert.HasCount(0, resolved.BlockedFiles); Assert.HasCount(0, resolved.ManualFiles!); var file = resolved.Files.Single(); Assert.IsTrue(file.FromManual);
        var requests = handler.Requests.Count; var target = Path.Combine(TestFiles.Root(), "staged.jar"); await resolver.DownloadAsync(file, target);
        CollectionAssert.AreEqual(asset.Bytes, await File.ReadAllBytesAsync(target)); Assert.AreEqual(requests, handler.Requests.Count);
        await File.WriteAllTextAsync(CurseForgeManualCache.PathFor(cache, manual), "changed cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.DownloadAsync(file, Path.Combine(TestFiles.Root(), "another.jar")));
    }
    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    public async Task ManualSupplementDoesNotBypassRejectedMalwareDeletedOrArchivedRestrictions(int status)
    {
        var asset = Mod(true) with { Available = false, FileStatus = status }; using var handler = new FixtureApi(asset); using var http = new HttpClient(handler);
        handler.Override = req => req.RequestUri!.Host == "api.modrinth.com" ? new(HttpStatusCode.NotFound) : null;
        var result = await new CurseForgePackResolver(http, FixtureKey, TestFiles.Root()).ResolveAsync([new(asset.Project, asset.FileId, true)], "1.21.1", "fabric");
        Assert.HasCount(0, result.Files); Assert.HasCount(1, result.BlockedFiles); Assert.IsFalse(result.ManualFiles?.Any() == true);
    }
}
