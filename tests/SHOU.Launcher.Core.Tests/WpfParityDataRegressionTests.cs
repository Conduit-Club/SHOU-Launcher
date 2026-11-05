using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfParityDataRegressionTests
{
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task RetryingOlderContentWithSharedHashKeepsItsOwnNameAndUri()
    {
        var downloader = new FailedTransfers();
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network expected.")));
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        await using var processes = (ProcessRunner)platform.Processes;
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        await service.InitializeAsync();
        var hash = new string('a', 40);
        var first = new ContentFile("first.jar", new("https://cdn.modrinth.com/first.jar"), hash, 1, true);
        var second = new ContentFile("second.jar", new("https://cdn.modrinth.com/second.jar"), hash, 1, true);
        await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadContentAsync(first));
        var firstTask = service.Snapshot.Downloads.Single().Id;
        await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadContentAsync(second));
        await Assert.ThrowsExactlyAsync<IOException>(() => service.RetryDownloadAsync(firstTask));
        Assert.AreEqual(3, downloader.Requests.Count);
        Assert.AreEqual(downloader.Requests[0].DestinationPath, downloader.Requests[2].DestinationPath);
        CollectionAssert.AreEqual(downloader.Requests[0].Sources.ToArray(), downloader.Requests[2].Sources.ToArray());
        Assert.AreNotEqual(downloader.Requests[1].DestinationPath, downloader.Requests[2].DestinationPath);
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task InitialMemoryCannotInvalidateAnExistingInstanceOverride()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network expected.")));
        var downloader = new FailedTransfers();
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        await using var processes = (ProcessRunner)platform.Processes;
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        await service.InitializeAsync();
        var instance = await service.CreateInstanceAsync("Limited memory", "1.21.1");
        await service.UpdateInstanceAsync(instance.Id, new(instance.Name, null, 1024));
        var before = service.Snapshot.Settings;
        var config = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        var bytes = await File.ReadAllBytesAsync(config);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SaveSettingsAsync(before with { MemoryMiB = 4096, MinimumMemoryMiB = 2048 }));
        Assert.AreEqual(before, service.Snapshot.Settings);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(config));
        await service.SaveSettingsAsync(before with { MinimumMemoryMiB = 1024 });
        Assert.AreEqual(1024, service.Snapshot.Settings.MinimumMemoryMiB);
    }

    [TestMethod]
    public async Task ExportRejectsAllowedNamesThatAliasPrivateFilesOrDirectories()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("POSIX symlink fixture; Windows validation is separate.");
        foreach (var directory in new[] { false, true })
        {
            var root = TestFiles.Root(); var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var protectedPath = Path.Combine(game, directory ? "private" : "credentials.json");
            if (directory) { Directory.CreateDirectory(protectedPath); await File.WriteAllTextAsync(Path.Combine(protectedPath, "credentials.json"), "private-fixture"); }
            else await File.WriteAllTextAsync(protectedPath, "private-fixture");
            if (directory) Directory.CreateSymbolicLink(Path.Combine(game, "config"), protectedPath);
            else File.CreateSymbolicLink(Path.Combine(game, "options.txt"), protectedPath);
            await Assert.ThrowsExactlyAsync<IOException>(() => InstanceWorkspace.ExportAsync(new("fixture", "fixture", "1.21.1", game), Path.Combine(root, "out"), false, default));
            Assert.AreEqual("private-fixture", await File.ReadAllTextAsync(directory ? Path.Combine(protectedPath, "credentials.json") : protectedPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "out")) && Directory.EnumerateFiles(Path.Combine(root, "out"), "*.zip").Any());
        }
    }

    private sealed class FailedTransfers : IVerifiedDownloader
    {
        public List<DownloadRequest> Requests { get; } = [];
        public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            progress?.Report(new(request.DestinationPath, "Downloading", 0, request.Size));
            throw new IOException("Fixture transfer failed.");
        }
    }
}
