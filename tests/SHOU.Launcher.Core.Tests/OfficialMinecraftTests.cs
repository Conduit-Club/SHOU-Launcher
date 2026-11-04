using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class OfficialMinecraftTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Official-1.21.1", name);
    private static JsonObject Metadata() => JsonNode.Parse(File.ReadAllText(Fixture("1.21.1.json")))!.AsObject();

    [TestMethod]
    public void OfficialMetadataAndAssetIndexMatchPinnedUpstreamHashes()
    {
        Assert.AreEqual("82ba6501da174a665c1787775f946487422f2432", Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(Fixture("1.21.1.json")))));
        Assert.AreEqual("4ca8e1a1edd37a703c5d1594f71d409852cd9770", Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(Fixture("17.json")))));
        var version = new ResolvedVersion("1.21.1", Metadata(), Fixture("1.21.1.json"), ["1.21.1"]);
        Assert.AreEqual(21, version.RequiredJavaMajor);
        Assert.AreEqual("java-runtime-delta", version.JavaComponent);
    }

    [TestMethod]
    public void ProductionResolverPreservesEveryOfficialModernMacNativeOnBothArchitectures()
    {
        foreach (var architecture in new[] { RuntimeArchitecture.Arm64, RuntimeArchitecture.X64 })
        {
            var context = new RuleContext("osx", "26.6", architecture);
            var result = LibraryResolver.Resolve(Metadata(), context);
            var expected = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Fixture(architecture == RuntimeArchitecture.Arm64 ? "expected-classpath-aarch64.json" : "expected-classpath-x86_64.json")))!;
            CollectionAssert.AreEqual(expected, result.ClasspathRelativePaths.ToArray());
            Assert.AreEqual(63, result.Files.Count);
            Assert.AreEqual(64939887L, result.Files.Sum(f => f.Size ?? 0));
            Assert.AreEqual(0, result.Files.Count(f => f.LegacyNative));
            Assert.AreEqual(16, result.Files.Count(f => f.Name.Contains(":natives-macos", StringComparison.Ordinal)));
            Assert.IsTrue(result.ClasspathRelativePaths.Any(p => p.EndsWith("natives-macos-patch.jar", StringComparison.Ordinal)));

            var root = TestFiles.Root();
            var version = new ResolvedVersion("1.21.1", Metadata(), Fixture("1.21.1.json"), ["1.21.1"]);
            var plan = LaunchPlanBuilder.Build(new(version,
                new("/fixture/java", "/fixture", "21.0.13", 21, "fixture", architecture, "fixture"), context,
                new("FixtureOnly", "00000000000000000000000000000001", "fixture-offline-token"),
                Path.Combine(root, "game"), Path.Combine(root, "libraries"), Path.Combine(root, "assets"),
                Path.Combine(root, "natives"), Path.Combine(root, "client.jar"), "17", 2048));
            Assert.AreEqual(64, plan.ClasspathEntries.Count);
            Assert.AreEqual(1, plan.Arguments.Count(a => a == "-XstartOnFirstThread"));
            Assert.IsFalse(plan.Arguments.Any(a => a.Contains("${", StringComparison.Ordinal)));
            CollectionAssert.Contains(plan.Arguments.ToArray(), "net.minecraft.client.main.Main");
        }
    }

    [TestMethod]
    public void OfficialAssetsUseHashDeduplicationWithoutDroppingLogicalNames()
    {
        var index = AssetResolver.Parse("17", File.ReadAllText(Fixture("17.json")));
        Assert.AreEqual(3911, index.Objects.Count);
        var unique = index.Objects.DistinctBy(o => o.Hash).ToArray();
        Assert.AreEqual(3888, unique.Length);
        Assert.AreEqual(826124658L, unique.Sum(o => o.Size));
        Assert.IsFalse(index.Virtual);
        Assert.IsFalse(index.MapToResources);
        Assert.AreEqual(26836906L, (long)Metadata()["downloads"]!["client"]!["size"]!);
    }
}
