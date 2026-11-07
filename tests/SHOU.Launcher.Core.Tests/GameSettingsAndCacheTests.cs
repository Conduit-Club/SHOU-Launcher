using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class GameSettingsAndCacheTests
{
    private static AppPaths Paths() => new(new(TestFiles.Root()), new NativePlatformInfo().Context);
    private static string Put(string root, string relative, string text = "fixture")
    { var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }

    [TestMethod]
    public async Task CompleteCachePreviewAndClearProtectInstalledDataExportsRecentAndBusyDownloads()
    {
        var paths = Paths();
        var asset = Put(paths.CacheDirectory, "assets/objects/aa/fixture", "asset");
        var library = Put(paths.CacheDirectory, "libraries/example/library.jar", "library");
        var oldPart = Put(paths.CacheDirectory, "content/old.jar.part", "stale"); File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddDays(-2));
        var recent = Put(paths.CacheDirectory, "content/new.jar.part");
        var busy = Put(paths.CacheDirectory, "libraries/busy.jar");
        var export = Put(paths.CacheDirectory, "exports/mypack.zip");
        var unknown = Put(paths.CacheDirectory, "user-files/keep.txt");
        var world = Put(paths.InstancesDirectory, "my-instance/saves/world/level.dat");
        var runtime = Put(paths.RuntimesDirectory, "java/lib/security/cacerts");
        var oldTemp = Put(paths.TempDirectory, "loader-old/staging.bin"); File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-2));
        var newTemp = Put(paths.TempDirectory, "game-fixture/current.bin");
        using var lease = new FileStream(busy + ".download.lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var plan = await LauncherCacheCleaner.ScanAsync(paths);
        Assert.AreEqual(4, plan.Files.Count); Assert.AreEqual(5 + 7 + 5 + 7, plan.Bytes);
        var result = await LauncherCacheCleaner.ClearAsync(paths, plan);
        Assert.AreEqual(4, result.DeletedFiles); Assert.AreEqual(plan.Bytes, result.FreedBytes);
        foreach (var deleted in new[] { asset, library, oldPart, oldTemp }) Assert.IsFalse(File.Exists(deleted));
        foreach (var kept in new[] { recent, busy, export, unknown, world, runtime, newTemp }) Assert.IsTrue(File.Exists(kept));
    }
    [TestMethod]
    public async Task ChangedCachePreviewRejectsWholePlanBeforeDeletingAnything()
    {
        var paths = Paths(); var first = Put(paths.CacheDirectory, "catalog/first.json"); var changed = Put(paths.CacheDirectory, "catalog/changed.json");
        var plan = await LauncherCacheCleaner.ScanAsync(paths); await File.WriteAllTextAsync(changed, "changed");
        await Assert.ThrowsAsync<IOException>(() => LauncherCacheCleaner.ClearAsync(paths, plan));
        Assert.IsTrue(File.Exists(first)); Assert.IsTrue(File.Exists(changed));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => LauncherCacheCleaner.ScanAsync(paths, cancelled.Token));
        Assert.IsTrue(File.Exists(first));
    }
    [TestMethod]
    public async Task CacheNeverFollowsLinksOrAcceptsForgedOutsidePlans()
    {
        var paths = Paths(); Directory.CreateDirectory(paths.CacheDirectory);
        var outside = Put(TestFiles.Root(), "secret.txt"); Directory.CreateSymbolicLink(Path.Combine(paths.CacheDirectory, "assets"), Path.GetDirectoryName(outside)!);
        Assert.IsEmpty((await LauncherCacheCleaner.ScanAsync(paths)).Files);
        var forged = new CacheCleanupPlan([new("cache", "../secret.txt", 7, new string('a', 64))], 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => LauncherCacheCleaner.ClearAsync(paths, forged)); Assert.IsTrue(File.Exists(outside));
    }
    [TestMethod]
    public void AutomaticMemoryHonorsAvailablePhysicalMemoryExplicitInstanceAndMinimum()
    {
        var instance = new GameInstance("fixture", "Fixture", "1.21.1", TestFiles.Root());
        var settings = new LauncherSettings(MemoryMiB: 2048, LaunchOptions: new(AutomaticMemory: true));
        var memory = new MemoryStatus(32L << 30, 16L << 30);
        Assert.AreEqual(9728, GameSettingsPolicy.MemoryMiB(instance, settings, memory));
        Assert.AreEqual(4096, GameSettingsPolicy.MemoryMiB(instance with { MemoryMiB = 4096 }, settings, memory));
        Assert.AreEqual(2048, GameSettingsPolicy.MemoryMiB(instance, settings, null));
        Assert.AreEqual(1536, GameSettingsPolicy.MemoryMiB(instance, settings, memory, RuntimeArchitecture.X86));
        Assert.AreEqual(2048, GameSettingsPolicy.MemoryMiB(instance with { LaunchOptions = new(AutomaticMemory: true, IpPreference: GameIpPreference.IPv6) }, settings with { LaunchOptions = new(AutomaticMemory: false) }, memory));
    }
    [TestMethod]
    public void SupportedJvmAndGameOptionsCannotOverrideCredentialsClasspathOrTls()
    {
        var options = new GameLaunchOptions(IpPreference: GameIpPreference.IPv4, JvmArguments: "-XX:+UseG1GC\n-Dfile.encoding=UTF-8");
        CollectionAssert.Contains(GameSettingsPolicy.JvmArguments(options).ToArray(), "-Djava.net.preferIPv4Stack=true");
        foreach (var bad in new[] { "-javaagent:evil.jar", "-Djavax.net.ssl.trustStore=bad", "-Dshou.launcher.sessionLog=bad", "-cp malicious.jar", "-XX:+DisableAttachMechanism", "-Dauth_access_token=secret" })
            Assert.ThrowsExactly<ArgumentException>(() => GameSettingsPolicy.Validate(options with { JvmArguments = bad }));
        CollectionAssert.AreEqual(new[] { "--server", "example.invalid", "--port", "25565" }, GameSettingsPolicy.GameArguments("--server example.invalid --port 25565").ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => GameSettingsPolicy.GameArguments("--accessToken secret"));
        Assert.ThrowsExactly<ArgumentException>(() => GameSettingsPolicy.GameArguments("--gameDir ../escape"));
        Assert.ThrowsExactly<ArgumentException>(() => GameSettingsPolicy.Validate(options with { BeforeLaunchExecutable = "relative.exe" }));
    }
    [TestMethod]
    public async Task QueuedDownloadCancellationReleasesBudgetAndConfiguredRateAppliesToBytes()
    {
        var budget = new DownloadBudget(); using var first = await budget.EnterAsync(1, default); using var cancelled = new CancellationTokenSource();
        var waiting = budget.EnterAsync(1, cancelled.Token); cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        first.Dispose(); using var reused = await budget.EnterAsync(1, default);
        var bytes = new byte[32768]; var options = new NetworkOptions(); options.Update(new(Downloads: new(MaximumKiBPerSecond: 64)));
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        var target = Path.Combine(TestFiles.Root(), "limited.bin"); var timer = System.Diagnostics.Stopwatch.StartNew();
        await new VerifiedDownloader(http, options).DownloadAsync(new([new Uri("https://example.invalid/file")], target, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes)), Size: bytes.Length));
        Assert.IsTrue(timer.Elapsed >= TimeSpan.FromMilliseconds(450)); CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
    }
    [TestMethod]
    public async Task HttpsOnlyDownloadPolicyRejectsRemoteHttpWithoutRequestingIt()
    {
        var options = new NetworkOptions(); options.Update(new(Downloads: new(HttpsOnly: true)));
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Insecure request must not be sent.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new VerifiedDownloader(http, options).DownloadAsync(new([new Uri("http://example.invalid/file")], Path.Combine(TestFiles.Root(), "blocked.bin"))));
    }
    private static string ExistingVersion(string root)
    {
        const string name = "中文 XPlus (Fabric)";
        var folder = Path.Combine(root, ".minecraft", "versions", name); Directory.CreateDirectory(folder);
        Put(folder, name + ".json", """{"id":"中文 XPlus (Fabric)","clientVersion":"1.21.1","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.10"}],"downloads":{"client":{"url":"https://piston-data.mojang.com/v1/objects/fixture/client.jar"}}} """);
        Put(folder, "mods/example.jar"); Put(folder, "saves/fixture/level.dat"); return folder;
    }
    [TestMethod]
    public async Task ExistingChineseSpaceProfileReadsOnlyVersionMetadataAndRetainsSourceIdentity()
    {
        var folder = ExistingVersion(TestFiles.Root()); var metadata = Path.Combine(folder, Path.GetFileName(folder) + ".json");
        var before = await File.ReadAllBytesAsync(metadata); Put(folder, "launcher_accounts.json", "do not read");
        var candidate = (await ExistingInstanceReader.InspectAsync(folder)).Single();
        Assert.AreEqual("1.21.1", candidate.GameVersion); Assert.AreEqual("fabric", candidate.Loader); Assert.AreEqual("0.16.10", candidate.LoaderVersion); Assert.AreEqual(folder, candidate.GameDirectory);
        var resolved = await ExistingInstanceReader.ResolveAsync(candidate.Origin, candidate.VersionId, candidate.GameVersion, default);
        CollectionAssert.Contains(resolved.InheritanceChain.ToArray(), "1.21.1"); Assert.AreEqual(candidate.VersionId, resolved.Id);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(metadata));
        await File.AppendAllTextAsync(metadata, " ");
        await Assert.ThrowsAsync<IOException>(() => ExistingInstanceReader.ResolveAsync(candidate.Origin, candidate.VersionId, candidate.GameVersion, default));
    }
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task AttachedFolderSettingsContentAndSelectionPersistWithoutNetworkingOrGameProcess()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network/game installation expected.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        var folder = ExistingVersion(platform.Paths.DataDirectory); string instanceId;
        await using (var service = new LauncherService(platform, http, downloader, new NetworkOptions()))
        {
            var candidate = (await service.InspectInstanceFolderAsync(folder)).Single(); var instance = await service.AttachExistingInstanceAsync(candidate); instanceId = instance.Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachExistingInstanceAsync(candidate));
            await service.UpdateInstanceAsync(instance.Id, new("本地实例", null, 4096, new(IpPreference: GameIpPreference.IPv6)));
            var mod = (await service.ListInstanceContentAsync(instance.Id, "mods")).Single();
            await service.SetInstanceModEnabledAsync(instance.Id, mod.RelativePath, false); Assert.IsTrue(File.Exists(Path.Combine(folder, "mods/example.jar.disabled")));
            await service.SetInstanceModEnabledAsync(instance.Id, "mods/example.jar.disabled", true);
            Assert.IsTrue(File.Exists(Path.Combine(folder, "mods/example.jar"))); Assert.IsTrue(File.Exists(Path.Combine(folder, "saves/fixture/level.dat")));
        }
        await using var restarted = new LauncherService(platform, http, downloader, new NetworkOptions()); await restarted.InitializeAsync();
        var restored = restarted.Snapshot.Instances.Single(); Assert.AreEqual(instanceId, restarted.Snapshot.SelectedInstanceId); Assert.AreEqual("本地实例", restored.Name); Assert.AreEqual(4096, restored.MemoryMiB); Assert.AreEqual(GameIpPreference.IPv6, restored.LaunchOptions!.IpPreference); Assert.AreEqual(folder, restored.GameDirectory);
        Assert.IsEmpty(restarted.Snapshot.GameSessions);
    }
    [TestMethod]
    public async Task NestedUnknownSettingsAndInstanceFieldsSurviveRoundTripWithWriteBeforeBackup()
    {
        var paths = Paths(); Directory.CreateDirectory(paths.LocalDataDirectory); var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
        var root = new JsonObject { ["schemaVersion"] = 1, ["settings"] = new JsonObject { ["launchOptions"] = new JsonObject { ["futureSetting"] = "keep" }, ["downloads"] = new JsonObject { ["futureDownload"] = true } }, ["instances"] = new JsonArray(), ["accounts"] = new JsonArray() };
        await File.WriteAllTextAsync(file, root.ToJsonString()); var original = await File.ReadAllTextAsync(file);
        await using var store = new ConfigurationStore(paths); var state = await store.LoadAsync();
        await store.SaveAsync(state with { Settings = state.Settings with { LaunchOptions = new(IpPreference: GameIpPreference.IPv4), Downloads = new(MaximumConnections: 3) } });
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(file))!; Assert.AreEqual("keep", (string?)saved["settings"]?["launchOptions"]?["futureSetting"]); Assert.IsTrue((bool)saved["settings"]!["downloads"]!["futureDownload"]!); Assert.AreEqual(original, await File.ReadAllTextAsync(file + ".bak"));
    }
    [TestMethod]
    public async Task FreshProfilesUseAutomaticMemoryButSavedLegacyMemoryRemainsManual()
    {
        var paths = Paths(); await using (var fresh = new ConfigurationStore(paths)) Assert.IsTrue((await fresh.LoadAsync()).Settings.LaunchOptions!.AutomaticMemory);
        Put(paths.LocalDataDirectory, "shou.desktop.v1.json", """{"settings":{"memoryMiB":4096},"instances":[],"accounts":[]} """);
        await using var legacy = new ConfigurationStore(paths); var settings = (await legacy.LoadAsync()).Settings;
        Assert.IsNull(settings.LaunchOptions); Assert.AreEqual(4096, GameSettingsPolicy.MemoryMiB(new("fixture", "Fixture", "1.21.1", paths.InstancesDirectory), settings, new(32L << 30, 16L << 30)));
    }
    [TestMethod]
    public async Task FollowingGlobalOptionsPreservesUnknownInstanceExtensionsAcrossRestart()
    {
        var paths = Paths(); var instance = new GameInstance("fixture", "Fixture", "1.21.1", paths.InstancesDirectory, LaunchOptions: new(IpPreference: GameIpPreference.IPv6));
        var state = new LauncherState(new(LaunchOptions: new(IpPreference: GameIpPreference.IPv4)), [instance], []);
        await using (var initial = new ConfigurationStore(paths)) await initial.SaveAsync(state);
        var path = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["instances"]![0]!["launchOptions"]!["futureInstanceOption"] = "keep"; await File.WriteAllTextAsync(path, json.ToJsonString());
        await using (var store = new ConfigurationStore(paths)) { state = await store.LoadAsync(); await store.SaveAsync(state with { Instances = [state.Instances[0] with { LaunchOptions = null }] }); }
        await using var restarted = new ConfigurationStore(paths); var restored = await restarted.LoadAsync();
        Assert.IsTrue(restored.Instances[0].LaunchOptions!.InheritGlobal); Assert.AreEqual(GameIpPreference.IPv4, GameSettingsPolicy.Options(restored.Instances[0], restored.Settings).IpPreference);
        Assert.AreEqual("keep", (string?)JsonNode.Parse(await File.ReadAllTextAsync(path))!["instances"]![0]!["launchOptions"]!["futureInstanceOption"]);
    }
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task ExplicitExternalFolderMappingSurvivesRestartWithoutRelaxingArbitraryGameRootWrites()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No network expected."))); var downloader = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        var folder = ExistingVersion(TestFiles.Root()); var metadata = Path.Combine(folder, Path.GetFileName(folder) + ".json"); var original = await File.ReadAllBytesAsync(metadata); string id;
        await using (var service = new LauncherService(platform, http, downloader, new NetworkOptions()))
        {
            await service.InitializeAsync(); await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveSettingsAsync(service.Snapshot.Settings with { GameRoot = TestFiles.Root() }));
            var instance = await service.AttachExistingInstanceAsync((await service.InspectInstanceFolderAsync(folder)).Single()); id = instance.Id;
            Assert.AreEqual(Path.Combine(folder, "mods"), await service.GetInstanceDirectoryAsync(id, "mods", true));
            Assert.AreEqual(Path.Combine(folder, "screenshots"), await service.GetInstanceDirectoryAsync(id, "screenshots", true));
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetInstanceDirectoryAsync(id, "../other", true));
        }
        await using var restarted = new LauncherService(platform, http, downloader, new NetworkOptions()); await restarted.InitializeAsync();
        Assert.AreEqual(folder, await restarted.GetInstanceDirectoryAsync(id)); CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(metadata));
    }
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task NewSharedPolicyDoesNotMoveExistingDataAndRetainsExplicitSelectionPreference()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("No install expected."))); var downloader = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        var isolated = await service.CreateInstanceAsync("isolated", "1.21.1"); Put(isolated.GameDirectory, "saves/keep/level.dat");
        await service.SaveSettingsAsync(service.Snapshot.Settings with { LaunchOptions = new(Isolation: InstanceIsolation.Shared), Downloads = new(SelectInstalledInstance: false) });
        var one = await service.CreateInstanceAsync("shared one", "1.21.1"); var two = await service.CreateInstanceAsync("shared two", "1.20.1");
        Assert.AreEqual(one.GameDirectory, two.GameDirectory); Assert.AreNotEqual(isolated.GameDirectory, one.GameDirectory); Assert.AreEqual(isolated.Id, service.Snapshot.SelectedInstanceId); Assert.IsTrue(File.Exists(Path.Combine(isolated.GameDirectory, "saves/keep/level.dat")));
    }
}
