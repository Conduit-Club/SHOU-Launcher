using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class FabricTests
{
    private static string Profile => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fabric-0.16.14", "profile.json"));
    [TestMethod] public void PinnedOfficialModernProfilePreservesParentJavaAndDeclaredLibraryHashes()
    {
        var profile = FabricMetadataProvider.Parse(Profile, "1.21.1", "0.16.14");
        Assert.AreEqual("b2089f5142944fc86d5ebbea35d65daa8bdcb1f9c99700c468cee0af9f01b363", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Profile))));
        var parent = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Official-1.21.1", "1.21.1.json")))!.AsObject();
        var merged = VersionMetadataResolver.Merge(parent, profile);
        var version = new ResolvedVersion("fabric-loader-0.16.14-1.21.1", merged, "fixture", ["1.21.1", "fabric-loader-0.16.14-1.21.1"]);
        Assert.AreEqual(21, version.RequiredJavaMajor); Assert.AreEqual("java-runtime-delta", version.JavaComponent);
        Assert.AreEqual("net.fabricmc.loader.impl.launch.knot.KnotClient", (string?)merged["mainClass"]);
        var resolved = LibraryResolver.Resolve(merged, new("osx", "26", RuntimeArchitecture.Arm64));
        Assert.AreEqual(71, resolved.Files.Count); Assert.AreEqual(0, resolved.Files.Count(f => f.LegacyNative));
        Assert.AreEqual(6, LibraryResolver.Resolve(profile, new("osx", "26", RuntimeArchitecture.Arm64)).Files.Count(f => f.Sha1 is not null && f.Size > 0));
    }
    [TestMethod] public async Task MalformedMirrorFallsThroughAndChangedCachedProfileIsNeverSilentlyUsed()
    {
        var calls = 0;
        using var handler = new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json(++calls == 1 ? Profile.Replace("\"inheritsFrom\":\"1.21.1\"", "\"inheritsFrom\":\"wrong\"") : Profile)));
        using var http = new HttpClient(handler); var root = TestFiles.Root();
        var provider = new FabricMetadataProvider(http, root, DownloadSources.Defaults);
        var id = await provider.CacheProfileAsync("1.21.1", "0.16.14"); Assert.AreEqual(2, calls);
        await provider.CacheProfileAsync("1.21.1", "0.16.14"); Assert.AreEqual(2, calls);
        var path = Path.Combine(root, "versions", id, id + ".json"); await File.AppendAllTextAsync(path, " ");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => provider.CacheProfileAsync("1.21.1", "0.16.14"));
        Assert.AreEqual(2, calls);
    }
    [TestMethod] public void UnknownParentAndEntryPointAreRejectedBeforeInstallingAnything()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => FabricMetadataProvider.Parse(Profile, "1.20.1", "0.16.14"));
        Assert.ThrowsExactly<InvalidDataException>(() => FabricMetadataProvider.Parse(Profile.Replace("net.fabricmc.loader.impl.launch.knot.KnotClient", "untrusted.Other"), "1.21.1", "0.16.14"));
        Assert.ThrowsExactly<ArgumentException>(() => FabricMetadataProvider.ProfileId("../escape", "0.16.14"));
    }
}
