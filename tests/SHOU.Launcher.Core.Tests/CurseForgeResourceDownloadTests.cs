using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CurseForgeResourceDownloadTests
{
    [TestMethod]
    public async Task LargeCachedPackUsesResourceBudgetWhileIndividualPackFilesRemainBounded()
    {
        var calls = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
        var resolver = new CurseForgePackResolver(http, null);
        var file = new ContentFile("stoneblock-fixture.zip", new("https://edge.forgecdn.net/files/1234/567/pack.zip"), new string('a',40), 216L * 1024 * 1024, true);
        var target = Path.Combine(TestFiles.Root(), "pack.zip");
        // A 216 MiB metadata-only fixture must reach the verified HTTP transport.
        var error = await Assert.ThrowsAsync<IOException>(() => resolver.DownloadResourceAsync(file, target));
        StringAssert.Contains(error.Message, "HTTP 403"); Assert.IsTrue(calls > 0); Assert.IsFalse(error.Message.Contains("项目 0"));
        calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.DownloadAsync(new(1,2,"mod.jar",file.Sha1,null,file.Size,file.Uri,false),target));
        Assert.AreEqual(0,calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.DownloadResourceAsync(file with { Size = CurseForgePackResolver.MaximumCachedResourceBytes + 1 },target));
        Assert.AreEqual(0,calls); Assert.IsFalse(File.Exists(target));
    }
}
