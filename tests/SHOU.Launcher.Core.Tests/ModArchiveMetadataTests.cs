using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ModArchiveMetadataTests
{
    [TestMethod]
    [DataRow("fabric.mod.json", "{\"name\":\"Fabric display\",\"version\":\"2.1\",\"icon\":\"assets/icon.png\"}", "Fabric display")]
    [DataRow("META-INF/mods.toml", "[[mods]]\ndisplayName=\"Forge display\"\nversion=\"2.1\"\nlogoFile=\"assets/icon.png\"", "Forge display")]
    [DataRow("mcmod.info", "[{\"name\":\"Legacy display\",\"version\":\"2.1\",\"logoFile\":\"assets/icon.png\"}]", "Legacy display")]
    public void ReadsDisplayNameVersionAndBoundedPngFromModArchive(string marker, string text, string title)
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "mods"));
        var path = Path.Combine(root, "mods", "fixture.jar");
        var icon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6RxQAAAAASUVORK5CYII=");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry(marker).Open()) output.Write(Encoding.UTF8.GetBytes(text));
            using (var output = zip.CreateEntry("assets/icon.png").Open()) output.Write(icon);
        }
        var row = InstanceWorkspace.List(new GameInstance("fixture", "Fixture", "1.21.1", root), "mods").Single();
        Assert.AreEqual(title, row.DisplayName); Assert.AreEqual("2.1", row.ModVersion); CollectionAssert.AreEqual(icon, row.IconPng); Assert.IsTrue(row.HasIcon);
    }
    [TestMethod]
    public void BadMetadataAndEscapingIconKeepUsableFileRows()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "mods", "broken.jar"), "not a zip");
        using (var zip = ZipFile.Open(Path.Combine(root, "mods", "safe.jar"), ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("fabric.mod.json").Open()) output.Write("{\"name\":\"Safe\",\"icon\":\"../secret.png\"}"u8);
        var rows = InstanceWorkspace.List(new GameInstance("fixture", "Fixture", "1.21.1", root), "mods");
        Assert.HasCount(2, rows); Assert.IsTrue(rows.All(r => !r.HasIcon)); Assert.AreEqual("Safe", rows.Single(r => r.Name == "safe.jar").DisplayName);
    }
}
