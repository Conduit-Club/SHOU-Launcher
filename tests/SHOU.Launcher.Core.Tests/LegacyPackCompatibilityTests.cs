using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LegacyPackCompatibilityTests
{
    private static string Archive(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(TestFiles.Root(), "legacy.zip"); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
        return path;
    }
    [TestMethod]
    [DataRow("hmcl")]
    [DataRow("mcbbs")]
    [DataRow("multimc")]
    [DataRow("multimc-no-cfg")]
    [DataRow("ordinary")]
    public async Task LegacyFormatsImportRealPayloadAndRemainRegisteredAfterRestart(string kind)
    {
        const string payload = "custom-mod-data/native.dll";
        var path = kind switch
        {
            "hmcl" => Archive(("wrap/modpack.json", "{\"name\":\"HMCL\",\"gameVersion\":\"1.21.1\"}"), ("wrap/minecraft/" + payload, "custom")),
            "mcbbs" => Archive(("mcbbs.packmeta", "{\"name\":\"MCBBS\",\"addons\":[{\"id\":\"game\",\"version\":\"1.21.1\"}],\"launchInfo\":{\"javaArgument\":[\"-Dfixture=true\"],\"launchArgument\":[\"--demo\"]}}"), ("overrides/" + payload, "custom")),
            "multimc" => Archive(("mmc-pack.json", "{\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}]}"), ("instance.cfg", "name=MultiMC\nMaxMemAlloc=4096\nOverrideJavaArgs=true\nJvmArgs=-Dfixture=true\nPreLaunchCommand=must-never-run"), (".minecraft/" + payload, "custom")),
            "multimc-no-cfg" => Archive(("mmc-pack.json", "{\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}]}"), ("minecraft/" + payload, "custom")),
            _ => Archive((".minecraft/versions/1.21.1/1.21.1.json", "{\"id\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]}"), (".minecraft/versions/1.21.1/1.21.1.jar", "bundled-client"), (".minecraft/" + payload, "custom"))
        };
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Import without prepare must not download or launch anything.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        GameInstance instance;
        await using (var service = new LauncherService(platform, http, downloader, new NetworkOptions()))
        {
            var inspect = await service.InspectLocalPackAsync(path); Assert.IsTrue(inspect.CanImport);
            var result = await service.ImportLocalPackAsync(new(path, inspect.ArchiveSha256, "Legacy fixture")); instance = result.Instance;
            Assert.AreEqual("custom", await File.ReadAllTextAsync(Path.Combine(instance.GameDirectory, payload)));
            if (kind is "mcbbs" or "multimc") Assert.AreEqual("-Dfixture=true", instance.LaunchOptions!.JvmArguments);
            if (kind == "multimc") Assert.AreEqual(4096, instance.MemoryMiB);
            if (kind == "ordinary")
            {
                var resolved = await new VersionMetadataResolver(http, downloader, platform.Paths.CacheDirectory, () => []).ResolveAsync(instance.VersionId);
                Assert.IsNotNull(resolved.Metadata["shouLocalClient"]);
                Assert.AreEqual("bundled-client", await File.ReadAllTextAsync(Path.Combine(platform.Paths.CacheDirectory, "versions", instance.VersionId, instance.VersionId + ".jar")));
            }
        }
        await using var restarted = new LauncherService(platform, http, downloader, new NetworkOptions()); await restarted.InitializeAsync();
        Assert.AreEqual(instance.GameDirectory, restarted.Snapshot.Instances.Single().GameDirectory);
    }
    [TestMethod]
    public async Task MultiMcPatchedLoaderAndBundledLibraryDoNotRequireADifferentLoaderDownload()
    {
        var source = Archive(("mmc-pack.json", "{\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.14\"}]}"),
            ("patches/net.fabricmc.fabric-loader.json", "{\"uid\":\"net.fabricmc.fabric-loader\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"+libraries\":[{\"name\":\"fixture:custom:1\"}]}"),
            ("libraries/fixture/custom/1/custom-1.jar", "bundled library"), (".minecraft/config/custom.json", "{}"));
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Patched loader must use the supplied metadata.")));
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        await using var service = new LauncherService(platform, http, downloader, new()); var inspect = await service.InspectLocalPackAsync(source);
        Assert.IsTrue(inspect.LegacyProfile!.Components!.Single().Overridden);
        var imported = await service.ImportLocalPackAsync(new(source, inspect.ArchiveSha256, "Patched"));
        var metadataPath = Path.Combine(platform.Paths.CacheDirectory, "versions", imported.Instance.VersionId, imported.Instance.VersionId + ".json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
        Assert.AreEqual("fabric", (string?)metadata["shouImportedLoader"]);
        var artifact = metadata["libraries"]![0]!["downloads"]!["artifact"]!;
        Assert.AreEqual(true, (bool?)artifact["shouGenerated"]);
        Assert.AreEqual("bundled library", await File.ReadAllTextAsync(Path.Combine(platform.Paths.CacheDirectory, "libraries", (string)artifact["path"]!)));
    }
    [TestMethod]
    public async Task ArchiveAndSinglePayloadCanExceedFormer256And128MiBLimitsWithStreamingCrcAndHash()
    {
        var path = Path.Combine(TestFiles.Root(), "large.zip"); const int blocks = 257;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("modpack.json").Open())) writer.Write("{\"name\":\"large\",\"gameVersion\":\"1.21.1\"}");
            using var output = zip.CreateEntry("minecraft/custom/data.bin", CompressionLevel.NoCompression).Open(); var buffer = new byte[1048576];
            for (var i = 0; i < blocks; i++) await output.WriteAsync(buffer);
        }
        var archive = new LocalPackArchive(); var inspected = await archive.ReadAsync(path);
        var stage = Path.Combine(TestFiles.Root(), "stage"); await archive.StageLegacyAsync(path, inspected.Inspection.ArchiveSha256, stage, default, null);
        Assert.AreEqual(blocks * 1048576L, new FileInfo(Path.Combine(stage, "custom/data.bin")).Length);
        var receipt = await ContentInstallFiles.ReadManifestAsync(new("unregistered", "Fixture", "1.21.1", stage)); Assert.HasCount(1, receipt.Files);
        Assert.IsTrue(await ContentInstallFiles.MatchesAsync(Path.Combine(stage, "custom/data.bin"), receipt.Files[0].Sha1, receipt.Files[0].Size, default));
        File.Delete(path); Directory.Delete(stage, true);
    }
}
