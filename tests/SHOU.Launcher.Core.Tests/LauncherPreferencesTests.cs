using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherPreferencesTests
{
    [TestMethod]
    public async Task ManualReleaseCheckOnlyReadsIndexAndNeverFetchesOrOffersInstallerAssets()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(request.Headers.Contains("x-api-key"));
            return Task.FromResult(ScriptedHttpHandler.Json("[{\"draft\":false,\"tag_name\":\"v9.0.0\",\"name\":\"Release\",\"body\":\"Release notes\",\"published_at\":\"2026-11-07T00:00:00Z\",\"html_url\":\"https://github.com/Conduit-Club/SHOU-Launcher/releases/tag/v9.0.0\",\"prerelease\":false,\"assets\":[{\"name\":\"installer.exe\"}]}]"));
        }));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), publicInformationHttp: http);
        var release = (await service.CheckLauncherReleasesAsync()).Single(); Assert.AreEqual("Release notes", release.Notes);
        Assert.AreEqual("https://github.com/Conduit-Club/SHOU-Launcher/releases/tag/v9.0.0", release.Page.AbsoluteUri);
        Assert.IsFalse(typeof(ILauncherInformationService).GetMethods().Any(m => m.Name.Contains("DownloadLauncher")));
        Assert.IsFalse(typeof(LauncherBehavior).GetProperties().Any(p => p.Name == "CheckUpdatesOnStartup"));
    }
    [TestMethod]
    public async Task PublicRedirectsCannotDowngradeOrForwardAuthentication()
    {
        var count = 0;
        using var http = new HttpClient(new InformationRedirectHandler(new ScriptedHttpHandler(request =>
        {
            count++; Assert.IsNull(request.Headers.Authorization); Assert.IsFalse(request.Headers.Contains("Cookie"));
            var result = new HttpResponseMessage(HttpStatusCode.Redirect); result.Headers.Location = new("http://github.com/insecure"); return Task.FromResult(result);
        })));
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://github.com/Conduit-Club/SHOU-Launcher/releases/download/v2/a.zip");
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer private-fixture"); message.Headers.TryAddWithoutValidation("Cookie", "fixture=private");
        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(message)); Assert.AreEqual(1, count);
    }
    [TestMethod]
    public async Task PublicRedirectsAllowOnlyReleaseCdnAndRetainRanges()
    {
        var requests = 0;
        using var http = new HttpClient(new InformationRedirectHandler(new ScriptedHttpHandler(request =>
        {
            Assert.AreEqual("bytes=4-", request.Headers.Range!.ToString()); requests++;
            if (requests == 1) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new("https://release-assets.githubusercontent.com/controlled-fixture"); return Task.FromResult(r); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("fixture") });
        })));
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://github.com/Conduit-Club/SHOU-Launcher/releases/download/v2/a.zip"); message.Headers.Range = new(4, null);
        using var response = await http.SendAsync(message); Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode); Assert.AreEqual(2, requests);
    }
    [TestMethod]
    public async Task LogArchiveRedactsJsonSecretsCookiesCredentialsAndQueryStrings()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "launcher.log"), "{\"access_token\":\"fixture-token-value\"}\nCookie: fixture-private-cookie\nhttps://user:fixture-password@example.invalid/path?key=fixture-private-query\nnormal line");
        File.WriteAllText(Path.Combine(root, "accounts.json"), "must not be exported");
        using var data = new MemoryStream(await LauncherLogArchive.ExportAsync(root)); using var zip = new ZipArchive(data);
        Assert.IsNull(zip.GetEntry("accounts.json")); using var reader = new StreamReader(zip.GetEntry("launcher.log")!.Open()); var text = reader.ReadToEnd();
        foreach (var secret in new[] { "fixture-token-value", "fixture-private-cookie", "fixture-password", "fixture-private-query" }) Assert.DoesNotContain(secret, text);
        Assert.Contains("normal line", text); Assert.Contains("https://example.invalid/path", text);
    }
    [TestMethod]
    public async Task LargeActiveLogExportsItsRecentTailAndStillRedactsSecrets()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "launcher.log"), "old omitted line\n" + new string('a', 1100000) + "\nrecent line\naccess_token=fixture-tail-secret\n");
        using var data = new MemoryStream(await LauncherLogArchive.ExportAsync(root)); using var zip = new ZipArchive(data);
        using var reader = new StreamReader(zip.GetEntry("launcher.log")!.Open()); var text = await reader.ReadToEndAsync();
        Assert.Contains("recent line", text); Assert.Contains("Earlier content omitted", text);
        Assert.DoesNotContain("old omitted line", text); Assert.DoesNotContain("fixture-tail-secret", text);
    }
    [TestMethod]
    public async Task HistoricalLogsAreRecoverableWhileCurrentRecentAndUnknownFilesRemain()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root);
        foreach (var name in new[] { "old.log", "launcher.log", "game-running.log", "accounts.json" }) { var path = Path.Combine(root, name); File.WriteAllText(path, name); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-9)); }
        File.WriteAllText(Path.Combine(root, "recent.log"), "recent");
        var plan = await LauncherLogArchive.ScanAsync(root); Assert.AreEqual("old.log", plan.Single().Name);
        var receipt = await LauncherLogArchive.RecycleAsync(root, plan); Assert.IsFalse(File.Exists(Path.Combine(root, "old.log"))); Assert.IsTrue(File.Exists(Path.Combine(root, "launcher.log")));
        await LauncherLogArchive.RestoreAsync(root, receipt); Assert.AreEqual("old.log", File.ReadAllText(Path.Combine(root, "old.log")));
        await LauncherLogArchive.RestoreAsync(root, receipt); Assert.AreEqual("old.log", File.ReadAllText(Path.Combine(root, "old.log")));
    }
    [TestMethod]
    public async Task ChangedHistoricalLogsRejectTheEntirePreview()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root); var file = Path.Combine(root, "old.log"); File.WriteAllText(file, "before"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-9));
        var plan = await LauncherLogArchive.ScanAsync(root); File.WriteAllText(file, "after!"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-9));
        await Assert.ThrowsAsync<IOException>(() => LauncherLogArchive.RecycleAsync(root, plan)); Assert.AreEqual("after!", File.ReadAllText(file));
    }
    [TestMethod]
    public async Task DnsOverHttpsParsesOnlyAddressAnswersAndPropagatesCancellation()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        { Assert.AreEqual("https", request.RequestUri!.Scheme); return Task.FromResult(ScriptedHttpHandler.Json("{\"Answer\":[{\"type\":5,\"data\":\"alias.invalid\"},{\"type\":1,\"data\":\"192.0.2.4\"}]}")); }));
        var addresses = await DnsOverHttpsConnector.ResolveAsync("fixture.example", http); Assert.AreEqual(IPAddress.Parse("192.0.2.4"), addresses.Single());
        using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => DnsOverHttpsConnector.ResolveAsync("fixture.example", http, cts.Token));
    }
}
