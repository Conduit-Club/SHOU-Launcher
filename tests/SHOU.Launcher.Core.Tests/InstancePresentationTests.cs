using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class InstancePresentationTests
{
    [TestMethod]
    public async Task PersonalizationPersistsAndKeepsUnknownPerInstanceAndPresentationKeys()
    {
        var paths = new AppPaths(new(TestFiles.Root()), new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "26"));
        Directory.CreateDirectory(paths.LocalDataDirectory); var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
        await File.WriteAllTextAsync(file, """{"instances":[{"id":"fixture","name":"fixture","versionId":"1.21.1","gameDirectory":"/fixture/game","futureInstance":{"keep":42},"presentation":{"futurePresentation":"keep","launchCount":7}}]}""");
        await using var store = new ConfigurationStore(paths); var state = await store.LoadAsync();
        var instance = state.Instances.Single();
        await store.SaveAsync(state with { Instances = [instance with { Presentation = instance.Presentation! with { Icon = "forge", Category = "测试", Description = "第一行\n第二行", Favorite = true } }] });
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!["instances"]![0]!;
        Assert.AreEqual(42, (int?)json["futureInstance"]?["keep"]); Assert.AreEqual("keep", (string?)json["presentation"]?["futurePresentation"]);
        var reloaded = await store.LoadAsync(); var presentation = reloaded.Instances.Single().Presentation!;
        Assert.AreEqual(7, presentation.LaunchCount); Assert.IsTrue(presentation.Favorite); Assert.AreEqual("测试", presentation.Category); Assert.AreEqual("第一行\n第二行", presentation.Description);
    }

    [TestMethod]
    public async Task InvalidPresentationAndDuplicateIdsDisableWritesWithoutReplacingOriginal()
    {
        foreach (var body in new[] { """[{"id":"fixture","name":"fixture","presentation":{"launchCount":-1}}]""", """[{"id":"fixture","name":"one"},{"id":"fixture","name":"two"}]""" })
        {
            var paths = new AppPaths(new(TestFiles.Root()), new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "26"));
            Directory.CreateDirectory(paths.LocalDataDirectory); var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
            var original = "{\"instances\":" + body + "}"; await File.WriteAllTextAsync(file, original);
            await using var store = new ConfigurationStore(paths);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(new(new(), [], [])));
            Assert.AreEqual(original, await File.ReadAllTextAsync(file));
        }
    }

    [TestMethod]
    public async Task FailedSaveKeepsPublishedMetadataAndOriginalCounter()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network expected.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        await service.InitializeAsync(); var instance = await service.CreateInstanceAsync("Fixture", "1.21.1");
        await service.UpdateInstancePresentationAsync(instance.Id, new("quilt", "测试", "saved", true));
        var persisted = service.Snapshot.Instances.Single().Presentation!;
        Assert.AreEqual(0, persisted.LaunchCount);
        var file = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json"); var backup = file + ".fixture-backup";
        File.Move(file, backup); Directory.CreateDirectory(file);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => service.UpdateInstancePresentationAsync(instance.Id, new("forge", "常用", "unsaved", false)));
            Assert.AreEqual(persisted, service.Snapshot.Instances.Single().Presentation);
        }
        finally { Directory.Delete(file); File.Move(backup, file); }
    }

    [TestMethod]
    public void BaseVersionIdentityIsExplicitAndLegacyFabricRoundTrips()
    {
        var fabric = new GameInstance("fixture", "fixture", "fabric-loader-0.16.14-1.21.1", "/fixture", Loader: "fabric", LoaderVersion: "0.16.14");
        Assert.AreEqual("1.21.1", InstanceIdentity.GameVersion(fabric));
        Assert.AreEqual("1.21.1", InstanceIdentity.GameVersion(fabric with { Loader = "forge", VersionId = "1.21.1-forge-52.1.21", GameVersionId = "1.21.1" }));
        Assert.ThrowsExactly<InvalidDataException>(() => InstanceIdentity.GameVersion(fabric with { Loader = "unknown" }));
    }
}
