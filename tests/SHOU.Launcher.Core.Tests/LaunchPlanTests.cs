using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LaunchPlanTests
{
    private static JsonObject Metadata() => JsonNode.Parse("""
        {"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":21,"component":"java-runtime-delta"},
        "libraries":[
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-macos","downloads":{"artifact":{"path":"org/lwjgl/x64.jar","url":"https://libraries.minecraft.net/x64.jar","sha1":"abc"}},"rules":[{"action":"allow","os":{"name":"osx"}}]},
          {"name":"org.lwjgl:lwjgl:3.3.3:natives-macos-arm64","downloads":{"artifact":{"path":"org/lwjgl/arm64.jar","url":"https://libraries.minecraft.net/arm64.jar","sha1":"def"}},"rules":[{"action":"allow","os":{"name":"osx"}}]}],
        "arguments":{"jvm":[{"rules":[{"action":"allow","os":{"name":"osx"}}],"value":["-XstartOnFirstThread"]},"-Djava.library.path=${natives_directory}","-cp","${classpath}"],
        "game":["--username","${auth_player_name}","--accessToken","${auth_access_token}","--gameDir","${game_directory}"]}}
        """)!.AsObject();
    private static LaunchInputs Inputs(string os = "osx")
    {
        var root = Path.Combine(TestFiles.Root(), "中文 路径 (test)"); Directory.CreateDirectory(root);
        return new(new("1.21.1", Metadata(), "fixture.json", ["1.21.1"]),
            new("/fixture/java", "/fixture", "21.0.13", 21, "test", RuntimeArchitecture.Arm64, "fixture"),
            new(os, "26.6", RuntimeArchitecture.Arm64), new("玩家 名", "12345678901234567890123456789012", "fake_sensitive_token"),
            Path.Combine(root, "game"), Path.Combine(root, "libraries"), Path.Combine(root, "assets"), Path.Combine(root, "natives"),
            Path.Combine(root, "client.jar"), "17", 2048);
    }
    [TestMethod] public void ModernNativeJarsStayOnClasspathAndArgvValuesRemainWhole()
    {
        var input = Inputs(); var plan = LaunchPlanBuilder.Build(input);
        Assert.AreEqual(3, plan.ClasspathEntries.Count);
        Assert.IsTrue(plan.ClasspathEntries.Any(p => p.EndsWith("x64.jar", StringComparison.Ordinal)));
        Assert.IsTrue(plan.ClasspathEntries.Any(p => p.EndsWith("arm64.jar", StringComparison.Ordinal)));
        Assert.AreEqual(1, plan.Arguments.Count(a => a == "-XstartOnFirstThread"));
        CollectionAssert.Contains(plan.Arguments.ToArray(), "玩家 名");
        CollectionAssert.Contains(plan.Arguments.ToArray(), input.GameDirectory);
        Assert.IsFalse(JsonSerializer.Serialize(plan.ToRedactedReport()).Contains("fake_sensitive_token", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(plan.ToRedactedReport()).Contains("玩家 名", StringComparison.Ordinal));
    }
    [TestMethod] public void TargetSeparatorAndJavaMetadataAreEnforced()
    {
        var input = Inputs("windows"); var plan = LaunchPlanBuilder.Build(input);
        var i = plan.Arguments.ToList().IndexOf("-cp");
        Assert.AreEqual(input.ClientJar, plan.Arguments[i + 1]); // osx-only libraries are excluded.
        Assert.IsFalse(plan.Arguments.Contains("-XstartOnFirstThread"));
        Assert.ThrowsExactly<InvalidOperationException>(() => LaunchPlanBuilder.Build(input with { Java = input.Java with { MajorVersion = 25 } }));
        var separator = new RuleContext("windows", "10.0", RuntimeArchitecture.X64).ClasspathSeparator;
        Assert.AreEqual(";", separator);
    }
    [TestMethod] public void UnknownJvmSecretsNeverEnterAnySerializedPlanReport()
    {
        var plan = LaunchPlanBuilder.Build(Inputs() with { ExtraJvmArguments = ["-Dhttp.proxyPassword=fake-extra-password"] });
        Assert.IsFalse(JsonSerializer.Serialize(plan.ToRedactedReport()).Contains("fake-extra-password", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(plan).Contains("fake-extra-password", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(plan).Contains("fake_sensitive_token", StringComparison.Ordinal));
    }
    [TestMethod] public void JavaAndNativeTemporaryFilesUseTheExplicitIsolatedRoot()
    {
        var input = Inputs(); var temporary = Path.Combine(TestFiles.Root(), "tmp 中文");
        var plan = LaunchPlanBuilder.Build(input with { TemporaryDirectory = temporary });
        CollectionAssert.Contains(plan.Arguments.ToArray(), "-Djava.io.tmpdir=" + temporary);
        Assert.AreEqual(temporary, plan.EnvironmentOverrides["TMPDIR"]);
        Assert.AreEqual(temporary, plan.EnvironmentOverrides["TEMP"]);
    }
    [TestMethod] public void ThirdPartyPlanRequiresInjectorAndNeverAddsWindowsTrustOnMac()
    {
        var input = Inputs(); var identity = input.Identity with { AuthenticationServer = new Uri("https://skin.mualliance.ltd/api/union/yggdrasil"), InjectorPath = "/fixture/injector space.jar", PrefetchedMetadataBase64 = "e30=" };
        var plan = LaunchPlanBuilder.Build(input with { Identity = identity });
        Assert.IsTrue(plan.Arguments.Any(a => a == "-javaagent:/fixture/injector space.jar=https://skin.mualliance.ltd/api/union/yggdrasil"));
        Assert.IsTrue(plan.Arguments.Contains("-Dauthlibinjector.side=client"));
        Assert.IsFalse(plan.Arguments.Any(a => a.Contains("WINDOWS-ROOT", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidOperationException>(() => LaunchPlanBuilder.Build(input with { Identity = identity with { InjectorPath = null } }));
    }
    [TestMethod] public async Task LegacyExtractionHonorsExcludesAndRejectsTraversal()
    {
        var root = TestFiles.Root(); var path = Path.Combine(root, "native.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/manifest").Open())) writer.Write("excluded");
            using (var writer = new StreamWriter(zip.CreateEntry("nested/libtest.dylib").Open())) writer.Write("fixture");
        }
        var files = await LibraryResolver.ExtractLegacyNativesAsync(path, Path.Combine(root, "out"), ["META-INF/"]);
        Assert.AreEqual(1, files.Count); Assert.IsTrue(File.Exists(Path.Combine(root, "out", "nested", "libtest.dylib")));
        var bad = Path.Combine(root, "bad.zip");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) zip.CreateEntry("../escape.dylib");
        await Assert.ThrowsExactlyAsync<IOException>(() => LibraryResolver.ExtractLegacyNativesAsync(bad, Path.Combine(root, "bad-out")));
    }
    [TestMethod] public void LegacyMetadataQuotedValuesAreParsedBeforeSubstitution()
    {
        CollectionAssert.AreEqual(new[] { "--name", "${auth_player_name}", "--value", "a b" }, LaunchPlanBuilder.TokenizeLegacy("--name ${auth_player_name} --value \"a b\"").ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => LaunchPlanBuilder.TokenizeLegacy("\"unterminated"));
    }
    [TestMethod] public void SavedWindowAndInitialMemoryOptionsReachTheProtectedPlansInput()
    {
        var input = Inputs() with { WindowWidth = 1280, WindowHeight = 720, Fullscreen = true,
            MinimumMemoryMiB = 1024, CustomInfo = "潮涌核心社" };
        input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("--versionType");
        input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("${version_type}");
        input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("--width");
        input.Version.Metadata["arguments"]!["game"]!.AsArray().Add("${resolution_width}");
        var plan = LaunchPlanBuilder.Build(input);
        var argv = plan.Arguments.ToList();
        Assert.AreEqual("1280", argv[argv.IndexOf("--width") + 1]);
        Assert.AreEqual(1, argv.Count(a => a == "--width"));
        Assert.AreEqual("720", argv[argv.IndexOf("--height") + 1]);
        CollectionAssert.Contains(argv, "--fullscreen");
        CollectionAssert.Contains(argv, "-Xms1024M");
        CollectionAssert.Contains(argv, "-Xmx2048M");
        Assert.AreEqual("潮涌核心社", argv[argv.IndexOf("--versionType") + 1]);
        Assert.IsFalse(JsonSerializer.Serialize(plan).Contains("fake_sensitive_token", StringComparison.Ordinal));
    }
    [TestMethod] public void InvalidWindowAndMemoryDraftsCannotBePersistedOrLaunched()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationStore.ValidateSettings(new(GameWindowWidth: 1280)));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationStore.ValidateSettings(new(GameWindowWidth: 1280, GameWindowHeight: 0)));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationStore.ValidateSettings(new(MinimumMemoryMiB: 4096)));
        Assert.ThrowsExactly<ArgumentException>(() => ConfigurationStore.ValidateSettings(new(CustomInfo: "line\nbreak")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LaunchPlanBuilder.Build(Inputs() with { MinimumMemoryMiB = 4096 }));
        ConfigurationStore.ValidateSettings(new(GameWindowWidth: 1280, GameWindowHeight: 720, MinimumMemoryMiB: 512));
    }
}
