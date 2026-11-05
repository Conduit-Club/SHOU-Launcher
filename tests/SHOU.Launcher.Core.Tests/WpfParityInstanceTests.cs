using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class WpfParityInstanceTests
{
    [TestMethod]
    public async Task ImportedModCanBeDisabledAndExportedWithNoAccountSidecar()
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
        var source = Path.Combine(root, "fixture mod.jar"); await File.WriteAllTextAsync(source, "test-only archive bytes");
        var instance = new GameInstance("fixture", "可移植实例", "1.21.1", game, Loader: "fabric");
        await InstanceWorkspace.ImportAsync(instance, "mods", source, default);
        var mod = InstanceWorkspace.List(instance, "mods").Single(); Assert.IsTrue(mod.Enabled);
        InstanceWorkspace.ToggleMod(instance, mod.RelativePath, false);
        mod = InstanceWorkspace.List(instance, "mods").Single(); Assert.IsFalse(mod.Enabled);
        await File.WriteAllTextAsync(Path.Combine(game, "credentials.json"), "must-not-export");
        var archive = await InstanceWorkspace.ExportAsync(instance, Path.Combine(root, "out"), false, default);
        using var zip = ZipFile.OpenRead(archive);
        Assert.IsNotNull(zip.GetEntry("mods/fixture mod.jar.disabled"));
        Assert.IsNotNull(zip.GetEntry("shou-instance.json")); Assert.IsNull(zip.GetEntry("credentials.json"));
    }
    [TestMethod]
    public async Task CollisionVanillaAndLinkEscapePreserveOriginalBytes()
    {
        var root = TestFiles.Root(); var game = Path.Combine(root, "game"); var mods = Path.Combine(game, "mods"); Directory.CreateDirectory(mods);
        var source = Path.Combine(root, "fixture.jar"); await File.WriteAllTextAsync(source, "new");
        var instance = new GameInstance("fixture", "fixture", "1.21.1", game, Loader: "fabric");
        await File.WriteAllTextAsync(Path.Combine(mods, "fixture.jar"), "original");
        await Assert.ThrowsExactlyAsync<IOException>(() => InstanceWorkspace.ImportAsync(instance, "mods", source, default));
        Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Combine(mods, "fixture.jar")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => InstanceWorkspace.ImportAsync(instance with { Loader = "vanilla" }, "mods", source, default));
        if (!OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(root, "protected.jar"); await File.WriteAllTextAsync(outside, "protected");
            File.CreateSymbolicLink(Path.Combine(mods, "linked.jar"), outside);
            Assert.IsFalse(InstanceWorkspace.List(instance, "mods").Any(e => e.Name == "linked.jar"));
            Assert.Throws<IOException>(() => InstanceWorkspace.ToggleMod(instance, "mods/linked.jar", false));
            Assert.AreEqual("protected", await File.ReadAllTextAsync(outside));
        }
    }
}
