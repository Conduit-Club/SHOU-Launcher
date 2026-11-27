using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Community;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CommunityShellTests
{
    private const string Question = "_minecraft._tcp.fixture.invalid";
    private static byte[] DnsPacket()
    {
        using var stream = new MemoryStream();
        stream.Write(Convert.FromHexString("123481800001000100000000"));
        foreach (var label in Question.Split('.')) { stream.WriteByte((byte)label.Length); stream.Write(Encoding.ASCII.GetBytes(label)); }
        stream.Write(Convert.FromHexString("0000210001c00c00210001000000000008000000057530c00c"));
        return stream.ToArray();
    }
    [TestMethod]
    public void SrvPortComesFromValidatedAnswerAndMalformedPointersAreRejected()
    {
        var packet = DnsPacket(); Assert.AreEqual(30000, MinecraftSrvResolver.ParsePort(packet, 0x1234, Question));
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftSrvResolver.ParsePort(packet, 0x5678, Question));
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftSrvResolver.ParsePort(packet, 0x1234, "different.invalid"));
        packet[12] = 0xc0; packet[13] = 12;
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftSrvResolver.ParsePort(packet, 0x1234, Question));
    }
    private static HttpResponseMessage Html(HttpRequestMessage request, string body) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "text/html") };
    private static readonly ClubUpdateNotice Notice = new(new(2026, 1, 2), "Fixture update", new("https://conduit-club.github.io/updates/2026-01-02/"));
    [TestMethod]
    public async Task ClubArticlesArePlainTextAndValidatedCacheSurvivesNetworkFailure()
    {
        var calls = 0; var offline = false;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            calls++; if (offline) throw new HttpRequestException("fixture offline");
            return Task.FromResult(Html(request, request.RequestUri!.AbsolutePath == "/updates/"
                ? "<article><a href='/updates/2026-01-02/'>2026.01.02 Fixture update</a></article>"
                : "<article><header>navigation</header><h1>Fixture update</h1><p>Readable &amp; safe.</p><script>secret-script</script></article>"));
        }));
        var client = new ClubUpdatesClient(http, TestFiles.Root());
        var history = await client.GetHistoryAsync(false, default); Assert.AreEqual(1, history.Notices.Count);
        var article = await client.GetArticleAsync(history.Notices[0], false, default);
        StringAssert.Contains(article.Body, "Readable & safe."); Assert.IsFalse(article.Body.Contains("secret-script"));
        offline = true;
        var fallback = await client.GetHistoryAsync(true, default); Assert.IsTrue(fallback.FromCache); Assert.IsNotNull(fallback.Warning);
        Assert.AreEqual(article, await client.GetArticleAsync(history.Notices[0], true, default));
        var before = calls; await client.GetArticleAsync(history.Notices[0], false, default); Assert.AreEqual(before, calls);
    }
    [TestMethod]
    public async Task NullCacheIsIgnoredAndUntrustedArticleNeverSendsRequest()
    {
        var root = TestFiles.Root(); await File.WriteAllTextAsync(Path.Combine(root, "index.json"), "{\"Notices\":null}");
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(Html(request, "<article><a href='/updates/2026-01-02/'>Fixture</a></article>"))));
        var client = new ClubUpdatesClient(http, root); Assert.AreEqual(1, (await client.GetHistoryAsync(false, default)).Notices.Count);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => client.GetArticleAsync(Notice with { Url = new("https://untrusted.invalid/updates/2026-01-02/") }, false, default));
    }
    [TestMethod]
    public async Task ClubRequestsHonorCancellationAndOversizedResponsesFail()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request => Task.FromResult(Html(request, new string('x', 2 * 1024 * 1024 + 1)))));
        var client = new ClubUpdatesClient(http, TestFiles.Root());
        await Assert.ThrowsExactlyAsync<IOException>(() => client.GetHistoryAsync(true, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetHistoryAsync(true, cancelled.Token));
    }
    [TestMethod]
    public void StandaloneMinecraftFilesRequireOfficialOriginSizeAndHash()
    {
        var metadata = JsonNode.Parse("""{"downloads":{"client":{"url":"https://piston-data.mojang.com/v1/objects/fixture/client.jar","sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":42}}}""")!.AsObject();
        var file = MinecraftPackages.Read("1.21.1", metadata).Single(); Assert.AreEqual("client", file.File.Kind); Assert.AreEqual(42, file.File.Size);
        metadata["downloads"]!["client"]!["url"] = "https://other.invalid/client.jar";
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftPackages.Read("1.21.1", metadata));
        metadata["downloads"]!["client"]!["url"] = file.Source.AbsoluteUri; metadata["downloads"]!["client"]!["sha1"] = "bad";
        Assert.ThrowsExactly<InvalidDataException>(() => MinecraftPackages.Read("1.21.1", metadata));
    }
}
