using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class InstanceModUpdateScanTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BudgetRetainsVerifiedResultsButExplicitUserCancellationStillCancels(bool userCancel)
    {
        var handler = new ScanHandler(); using var http = new HttpClient(handler); var downloader = new VerifiedDownloader(http);
        var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        var game = Path.Combine(platform.Paths.InstancesDirectory, "fixture"); Directory.CreateDirectory(Path.Combine(game, "mods"));
        for (var i = 0; i < 8; i++) await File.WriteAllBytesAsync(Path.Combine(game, "mods", $"{i}.jar"), [(byte)i]);
        var instance = new GameInstance("fixture", "Fixture", "fabric-loader-0.16.14-1.21.1", game, Loader: "fabric", LoaderVersion: "0.16.14", Installed: true, GameVersionId: "1.21.1");
        await using (var store = new ConfigurationStore(platform.Paths)) await store.SaveAsync(new(new(), [instance], []));
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        if (userCancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        if (userCancel)
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckInstanceModUpdatesAsync(instance.Id, TimeSpan.FromSeconds(10), cancellation.Token));
        else
        {
            var reports = new List<PackInstallProgress>();
            var progress = new SHOU.Launcher.Core.Content.PackProgressReporter(new ScanProgress(reports.Add));
            var result = await service.CheckInstanceModUpdatesAsync(instance.Id, TimeSpan.FromSeconds(2), progress: progress);
            Assert.AreEqual(0, reports[0].CompletedFiles); Assert.AreEqual(8, reports[0].TotalFiles);
            Assert.AreEqual(result.CheckedFiles, reports[^1].CompletedFiles);
            Assert.IsTrue(reports.Zip(reports.Skip(1)).All(pair => pair.First.CompletedFiles <= pair.Second.CompletedFiles));
            Assert.HasCount(1, result.Updates); Assert.IsTrue(result.Updates[0].RelativePath.StartsWith("mods/", StringComparison.Ordinal));
            Assert.HasCount(7, result.Notices); Assert.IsTrue(result.CheckedFiles is > 0 and < 8);
            Assert.IsTrue(result.Notices.All(n => n.Contains("未改动此文件", StringComparison.Ordinal)));
        }
        Assert.AreEqual(8, Directory.GetFiles(Path.Combine(game, "mods")).Length);
        Assert.IsFalse(File.Exists(Path.Combine(game, ".shou-content-manifest.json")));
    }

    private sealed class ScanProgress(Action<PackInstallProgress> report) : IProgress<PackInstallProgress>
    { public void Report(PackInstallProgress value) => report(value); }

    private sealed class ScanHandler : HttpMessageHandler
    {
        private string? firstHash;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/version_file/", StringComparison.Ordinal)) Interlocked.CompareExchange(ref firstHash, path[(path.LastIndexOf('/') + 1)..], null);
            var hash = firstHash ?? "";
            object Release(bool newer) => new { id = newer ? "next" : "old", project_id = "fixture", name = "Fixture", version_number = newer ? "2" : "1",
                date_published = newer ? "2026-01-02T00:00:00Z" : "2026-01-01T00:00:00Z", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, version_type = "release",
                files = new[] { new { filename = newer ? "new.jar" : "0.jar", url = "https://cdn.modrinth.com/data/fixture/versions/old/0.jar", hashes = new { sha1 = newer ? new string('a', 40) : hash }, size = 1, primary = true } }, dependencies = Array.Empty<object>() };
            string body;
            if (path.EndsWith("/v2/version_file/" + hash, StringComparison.Ordinal)) body = JsonSerializer.Serialize(Release(false));
            else if (path.EndsWith("/v2/project/fixture/version", StringComparison.Ordinal)) body = JsonSerializer.Serialize(new[] { Release(true) });
            else { await Task.Delay(Timeout.Infinite, ct); throw new AssertFailedException("Blocked request unexpectedly resumed."); }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
