using System.Net;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Downloads;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CurseForgeResourceDownloadTests
{
    [TestMethod]
    public async Task CdnTransportSharesTheGlobalBudgetAndCancellationWhileQueued()
    {
        var calls = 0; var bytes = "verified CDN fixture"u8.ToArray();
        using var http = new HttpClient(new ScriptedHttpHandler(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
        var options = new NetworkOptions(); options.Update(new(Downloads: new(MaximumConnections: 1)));
        var resolver = new CurseForgePackResolver(http, null, network: options);
        var file = new ContentFile("pack.zip", new("https://edge.forgecdn.net/files/1234/567/pack.zip"), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true);
        var path = Path.Combine(TestFiles.Root(), "pack.zip");
        using (var occupied = await options.Budget.EnterAsync(1, CancellationToken.None))
        using (var cancel = new CancellationTokenSource())
        {
            var pending = resolver.DownloadResourceAsync(file, path, ct: cancel.Token);
            Assert.IsFalse(pending.IsCompleted); Assert.AreEqual(0, calls); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        await resolver.DownloadResourceAsync(file, path);
        Assert.AreEqual(1, calls); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
    }

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
