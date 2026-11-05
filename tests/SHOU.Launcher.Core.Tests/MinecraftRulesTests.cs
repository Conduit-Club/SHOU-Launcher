using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class MinecraftRulesTests
{
    private static readonly RuleContext Mac = new("osx", "26.6", RuntimeArchitecture.Arm64);
    private static bool Check(string json, RuleContext? context = null) => MinecraftRuleEvaluator.Allows(JsonNode.Parse(json)!.AsObject(), context ?? Mac);
    [TestMethod] public void MissingRulesAllowButPresentEmptyRulesDeny()
    { Assert.IsTrue(Check("{}")); Assert.IsFalse(Check("{\"rules\":[]}")); }
    [TestMethod] public void LastMatchingRuleWins()
    {
        Assert.IsFalse(Check("""{"rules":[{"action":"allow"},{"action":"disallow","os":{"name":"osx"}},{"action":"allow","os":{"name":"windows"}}]}"""));
        Assert.IsTrue(Check("""{"rules":[{"action":"disallow"},{"action":"allow","os":{"name":"osx","arch":"aarch64","version":"^26\\."}}]}"""));
    }
    [TestMethod] public void FeaturesUseExplicitFalseDefaultAndArchitectureAliases()
    {
        Assert.IsTrue(Check("""{"rules":[{"action":"allow","features":{"is_demo_user":false},"os":{"arch":"arm64"}}]}"""));
        Assert.IsFalse(Check("""{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}]}"""));
        Assert.IsTrue(Check("""{"rules":[{"action":"allow","features":{"has_custom_resolution":true}}]}""", Mac with { Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true } }));
        Assert.IsFalse(Check("""{"rules":[{"action":"allow","os":{"arch":"x86_64"}}]}"""));
    }
    [TestMethod] public void MetadataMergeOverridesLibraryVersionAndAppendsArguments()
    {
        var parent = JsonNode.Parse("""{"id":"parent","mainClass":"Parent","arguments":{"jvm":["-XstartOnFirstThread"],"game":["--parent"]},"libraries":[{"name":"g:a:1"},{"name":"g:b:1"}]}""")!.AsObject();
        var child = JsonNode.Parse("""{"id":"child","inheritsFrom":"parent","mainClass":"Child","arguments":{"game":["--child"]},"libraries":[{"name":"g:a:2"}]}""")!.AsObject();
        var result = VersionMetadataResolver.Merge(parent, child);
        Assert.AreEqual("Child", (string?)result["mainClass"]);
        CollectionAssert.AreEqual(new[] { "g:b:1", "g:a:2" }, result["libraries"]!.AsArray().Select(n => (string?)n?["name"]).ToArray());
        CollectionAssert.AreEqual(new[] { "--parent", "--child" }, result["arguments"]!["game"]!.AsArray().Select(n => (string?)n).ToArray());
        Assert.AreEqual(1, parent["arguments"]!["game"]!.AsArray().Count);
    }
    [TestMethod] public async Task CyclicMetadataFailsBeforeNetwork()
    {
        var root = TestFiles.Root();
        foreach (var (id, parent) in new[] { ("a", "b"), ("b", "a") })
        { var p = Path.Combine(root, "versions", id); Directory.CreateDirectory(p); await File.WriteAllTextAsync(Path.Combine(p, id + ".json"), $$"""{"id":"{{id}}","inheritsFrom":"{{parent}}"}"""); }
        using var http = new HttpClient();
        var resolver = new VersionMetadataResolver(http, new Downloads.VerifiedDownloader(http), root, PCL.Core.Conduit.DownloadSources.Defaults);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.ResolveAsync("a"));
    }
}

internal static class TestFiles
{
    // Moving a regular staging file over a directory is EISDIR/IOException on Unix
    // and ERROR_ACCESS_DENIED/UnauthorizedAccessException on Windows. Keep all
    // transaction/rollback assertions while checking the host's concrete failure.
    public static async Task AssertDirectoryCommitFailureAsync(Func<Task> action)
    {
        if (OperatingSystem.IsWindows()) await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        else await Assert.ThrowsAsync<IOException>(action);
    }

    public static string Root()
    {
        var basis = Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Tests require guarded task environment.");
        var path = Path.Combine(basis, "test-data", "core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
