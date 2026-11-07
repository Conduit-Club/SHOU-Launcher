using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class VendorLoaderCatalogTests
{
    [TestMethod, DataRow("valid"), DataRow("wrong-loader"), DataRow("wrong-game"), DataRow("wrong-version"), DataRow("corrupt-library")]
    public async Task VendorReceiptReuseRequiresExactIdentityAndBytesWithoutNetwork(string scenario)
    {
        var root = TestFiles.Root(); var cache = Path.Combine(root, "cache");
        const string version = "production-49-commit", id = "shou-labymod-1.21.1-production-49-commit";
        var recipeRoot = Path.Combine(cache, "loaders/vendor/labymod/1.21.1", version); Directory.CreateDirectory(recipeRoot);
        var profile = Path.Combine(cache, "versions", id, id + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        var library = Path.Combine(cache, "libraries/example/library.jar"); Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        await File.WriteAllTextAsync(Path.Combine(recipeRoot, "installer.jar"), "public fixture installer");
        await File.WriteAllTextAsync(profile, "{}"); await File.WriteAllTextAsync(library, "public fixture library");
        string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var receipt = new { Loader = scenario == "wrong-loader" ? "optifine" : "labymod", Game = scenario == "wrong-game" ? "1.20.1" : "1.21.1",
            Version = scenario == "wrong-version" ? "production-48-commit" : version, Artifact = "commit", InstallerHash = Hash(Path.Combine(recipeRoot, "installer.jar")), ProfileHash = Hash(profile), Libraries = new Dictionary<string, string> { ["example/library.jar"] = Hash(library) }, Payload = new Dictionary<string, string>() };
        await File.WriteAllTextAsync(Path.Combine(recipeRoot, "installed.json"), System.Text.Json.JsonSerializer.Serialize(receipt, Configuration.ConfigurationStore.JsonOptions));
        if (scenario == "corrupt-library") await File.AppendAllTextAsync(library, "changed");
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Receipt reuse must stay offline")));
        var downloads = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(root), downloads);
        await using var runner = (ProcessRunner)platform.Processes;
        var type = typeof(LauncherService).Assembly.GetType("SHOU.Launcher.Core.Loaders.PortableVendorInstaller")!;
        var installer = Activator.CreateInstance(type, [http, downloads, runner, cache, Path.Combine(root, "tmp"), (Func<IReadOnlyList<PCL.Core.Conduit.DownloadSource>>)(() => PCL.Core.Conduit.DownloadSources.Defaults()), (Func<ProxySettings>)(() => new(ProxyMode.Direct)), null]);
        var operation = (Task<string?>)type.GetMethod("CachedProfileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(installer, ["labymod", "1.21.1", version, CancellationToken.None])!;
        Assert.AreEqual(scenario == "valid" ? id : null, await operation);
    }
    [TestMethod]
    public async Task LabyModChoicesPinReleaseCommitAndHideUnsupportedGameVersions()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            Assert.AreEqual("releases.r2.labymod.net", request.RequestUri!.Host);
            Assert.IsFalse(request.Headers.Contains("x-api-key"));
            return Task.FromResult(ScriptedHttpHandler.Json("""{"labyModVersion":"49","commitReference":"c228c3a3","minecraftVersions":[{"version":"1.21.1","runtime":{"name":"java-runtime-delta","version":21}}]}"""));
        }));
        var downloads = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloads);
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var runner = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloads, new NetworkOptions());
        var choices = await service.GetLoaderVersionsAsync("labymod", "1.21.1");
        Assert.HasCount(2, choices); StringAssert.Contains(choices[0].Version, "c228c3a3");
        Assert.AreNotEqual(choices[0].Version, choices[0].DisplayVersion); StringAssert.Contains(choices[0].DisplayVersion, "49");
        Assert.IsEmpty(await service.GetLoaderVersionsAsync("labymod", "1.20.1"));
        Assert.IsEmpty(service.Snapshot.Instances);
    }
}
