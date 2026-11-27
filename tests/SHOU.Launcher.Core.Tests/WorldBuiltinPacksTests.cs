using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WorldBuiltinPacksTests
{
    [TestMethod]
    [DataRow("nested", true)]
    [DataRow("convention", true)]
    [DataRow("missing-convention-data", false)]
    [DataRow("standalone", true)]
    [DataRow("wrong-loader", false)]
    [DataRow("unknown-version", false)]
    [DataRow("untracked", false)]
    [DataRow("changed", false)]
    [DataRow("missing-provider", false)]
    public async Task WorldFabricIdentityIsBoundToTheTargetLoaderAndUnchangedTrackedModule(string scenario, bool allowed)
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "mods"));
        var instance = new GameInstance("fixture", "QA", "fabric-profile", root, Loader: scenario == "wrong-loader" ? "vanilla" : "fabric", GameVersionId: scenario == "unknown-version" ? "1.21.2" : "1.21.1");
        var module = new Dictionary<string, byte[]> { ["fabric.mod.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"id\":\"fabric-resource-loader-v0\",\"version\":\"fixture\"}") };
        if (scenario != "missing-provider") module["net/fabricmc/fabric/impl/resource/loader/ModResourcePackCreator.class"] = [1, 2, 3];
        var nested = Zip(module);
        var needsConvention = scenario is "convention" or "missing-convention-data";
        var tags = new Dictionary<string, byte[]> { ["fabric.mod.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"id\":\"fabric-convention-tags-v2\"}") };
        if (scenario == "convention") tags["data/c/tags/item/qa.json"] = Encoding.UTF8.GetBytes("{\"values\":[]}");
        var bytes = scenario == "standalone" ? nested : Zip(new()
        {
            ["fabric.mod.json"] = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"id\":\"fabric-api\",\"jars\":[{\"file\":\"META-INF/jars/fabric-resource-loader-v0-fixture.jar\"},{\"file\":\"META-INF/jars/fabric-convention-tags-v2-fixture.jar\"}]}"),
            ["META-INF/jars/fabric-resource-loader-v0-fixture.jar"] = nested,
            ["META-INF/jars/fabric-convention-tags-v2-fixture.jar"] = Zip(tags)
        });
        var relative = "mods/" + (scenario == "standalone" ? "fabric-resource-loader-v0-fixture.jar" : "fabric-api-fixture.jar");
        var path = Path.Combine(root, relative); await File.WriteAllBytesAsync(path, bytes);
        var manifest = new ContentInstallManifest(1, instance.Id, [new("fixture", "fixture", "mod", relative, Convert.ToHexStringLower(SHA1.HashData(bytes)), Convert.ToHexStringLower(SHA512.HashData(bytes)), bytes.Length)]);
        if (scenario != "untracked") await File.WriteAllBytesAsync(Path.Combine(root, ContentInstallFiles.ManifestName), ContentInstallFiles.SerializeManifest(manifest));
        if (scenario == "changed") await File.AppendAllTextAsync(path, "changed");
        var verified = await WorldBuiltinPacks.ResolveAsync(instance, default); Assert.AreEqual(allowed || needsConvention, verified.Contains("fabric"));
        Assert.AreEqual(scenario == "convention", verified.Contains("fabric-convention-tags-v2"));
        var world = Path.Combine(root, "world"); Directory.CreateDirectory(world);
        var enabled = new List<NbtTag> { new(NbtType.String, "", "vanilla"), new(NbtType.String, "", "fabric") };
        if (needsConvention) enabled.Add(new(NbtType.String, "", "fabric-convention-tags-v2"));
        var original = BoundedNbt.Write(new(new(NbtType.Compound, "", new NbtTag[] { new(NbtType.Compound, "Data", new NbtTag[] { new(NbtType.Compound, "DataPacks", new NbtTag[] { new(NbtType.List, "Enabled", new NbtList(NbtType.String, enabled)) }) }) }), NbtCompression.Gzip));
        var levelPath = Path.Combine(world, "level.dat"); await File.WriteAllBytesAsync(levelPath, original);
        if (allowed) await WorldContentArchive.ValidateBundledWorldPacksAsync(world, new(48, 0), default, verified);
        else await Assert.ThrowsExactlyAsync<NotSupportedException>(() => WorldContentArchive.ValidateBundledWorldPacksAsync(world, new(48, 0), default, verified));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(levelPath));
        if (allowed)
        {
            await File.AppendAllTextAsync(path, "modified-after-preview");
            var after = await WorldBuiltinPacks.ResolveAsync(instance, default);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => WorldContentArchive.ValidateBundledWorldPacksAsync(world, new(48, 0), default, after));
        }
    }
    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in files) using (var entry = zip.CreateEntry(name).Open()) entry.Write(bytes);
        return output.ToArray();
    }
}
