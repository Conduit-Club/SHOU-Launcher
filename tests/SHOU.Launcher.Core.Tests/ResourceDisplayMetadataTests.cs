using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ResourceDisplayMetadataTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6RxQAAAAASUVORK5CYII=");
    private static GameInstance Pack(string kind, string metadata, string icon = "pack.png", byte[]? bytes = null)
    {
        var i = new GameInstance("fixture", "Fixture", "1.21.1", TestFiles.Root()); var folder = InstanceWorkspace.Folder(i.GameDirectory, kind); Directory.CreateDirectory(folder);
        using var zip = ZipFile.Open(Path.Combine(folder, "Display Pack.zip"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open())) writer.Write(metadata);
        using (var output = zip.CreateEntry(icon).Open()) output.Write(bytes ?? Png);
        return i;
    }
    [TestMethod]
    public void LocalIconAndFormattedDescriptionAreReadWithoutExtractingOrRewritingPack()
    {
        var i = Pack("resourcepacks", "{\"pack\":{\"description\":{\"text\":\"First\\n\",\"extra\":[{\"text\":\"second\"}]}}}");
        var path = Path.Combine(InstanceWorkspace.Folder(i.GameDirectory, "resourcepacks"), "Display Pack.zip"); var before = File.ReadAllBytes(path);
        var row = InstanceWorkspace.List(i, "resourcepacks").Single(); Assert.AreEqual("Display Pack", row.DisplayName); Assert.AreEqual("First second", row.Description); CollectionAssert.AreEqual(Png, row.IconPng);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); Assert.HasCount(1, Directory.GetFiles(i.GameDirectory, "*", SearchOption.AllDirectories)); Assert.IsNull(row.MetadataError);
    }
    [TestMethod]
    [DataRow("resourcepacks", "pack.png")]
    [DataRow("shaders", "icon.png")]
    public void MalformedDescriptionDoesNotSuppressValidIconOrTheUsableFileRow(string kind, string icon)
    {
        var i = Pack(kind, "not json", icon); var row = InstanceWorkspace.List(i, kind).Single();
        Assert.IsTrue(row.HasIcon); Assert.AreEqual("Display Pack.zip", row.Name); Assert.IsNull(row.MetadataError);
    }
    [TestMethod]
    public void OversizedIconIsSkippedWithoutDisablingOrFilteringPack()
    {
        var i = Pack("resourcepacks", "{\"pack\":{\"description\":\"Readable\"}}", bytes: new byte[256 * 1024 + 1]);
        var row = InstanceWorkspace.List(i, "resourcepacks").Single(); Assert.IsFalse(row.HasIcon); Assert.IsTrue(row.Enabled); Assert.AreEqual("Readable", row.Description); Assert.IsNull(row.MetadataError);
    }
    [TestMethod]
    public async Task InvalidArchiveRemainsImportableAndCanBeShownAsAReadError()
    {
        var root = TestFiles.Root(); var path = Path.Combine(root, "broken.zip"); await File.WriteAllTextAsync(path, "not a zip");
        var i = new GameInstance("fixture", "Fixture", "1.21.1", TestFiles.Root()); await InstanceWorkspace.ImportAsync(i, "shaders", path, default);
        var row = InstanceWorkspace.List(i, "shaders").Single(); Assert.IsNotNull(row.MetadataError); Assert.AreEqual("broken.zip", row.Name);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(row.FullPath));
    }
}
