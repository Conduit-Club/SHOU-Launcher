using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ManualReleaseFallbackTests
{
    [TestMethod, DataRow("good"), DataRow("foreign"), DataRow("dtd")]
    public async Task ManualRateLimitFallbackReadsOnlyBoundedPublicFeedAndNeverInstallerOrExternalEntities(string mode)
    {
        var requests = 0;
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            requests++; Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(request.Headers.Contains("Cookie")); Assert.IsFalse(request.Headers.Contains("x-api-key"));
            Assert.AreEqual("https", request.RequestUri!.Scheme);
            if (request.RequestUri.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            Assert.AreEqual("https://github.com/Conduit-Club/SHOU-Launcher/releases.atom", request.RequestUri.AbsoluteUri);
            var page = mode == "foreign" ? "https://example.invalid/capture" : "https://github.com/Conduit-Club/SHOU-Launcher/releases/tag/v2.0";
            var xml = (mode == "dtd" ? "<!DOCTYPE feed [<!ENTITY entity SYSTEM 'file:///must-never-read'>]>" : "") + $"<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>Release</title><link rel='alternate' href='{page}'/><updated>2026-11-07T00:00:00Z</updated><content type='html'>&lt;p&gt;Manual &amp;amp; safe&lt;/p&gt;</content></entry></feed>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        }));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        await using var processes = (ProcessRunner)platform.Processes; await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), publicInformationHttp: http);
        if (mode == "good")
        {
            var release = (await service.CheckLauncherReleasesAsync()).Single(); Assert.AreEqual("v2.0", release.Tag);
            Assert.AreEqual("Manual & safe", release.Notes); Assert.IsNull(release.Prerelease);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckLauncherReleasesAsync());
        Assert.AreEqual(2, requests);
    }
}
