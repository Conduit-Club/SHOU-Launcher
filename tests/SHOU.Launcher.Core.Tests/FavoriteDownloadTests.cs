using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class FavoriteDownloadTests
{
    private static ContentProject Project(string id, string kind = "mod") => new(id, id, id, "description", "author", 1, kind, null, new("https://modrinth.com/mod/" + id));
    private static ContentFile FileOf(string name, byte[] bytes) => new(name, new("https://cdn.modrinth.com/data/fixture/" + name), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true);
    [TestMethod]
    public void CommonVersionsUseEachResourceAndTheChosenLoaderAndPickLatestActualRelease()
    {
        var a = Project("a"); var b = Project("b", "resourcepack"); var first = FileOf("same.zip", [1]); var second = FileOf("same.zip", [2]); var date = DateTimeOffset.UtcNow;
        Dictionary<string, IReadOnlyList<ContentRelease>> releases = new()
        {
            ["a"] = [new("a1", "one", "one", date.AddDays(-1), ["1.21.1", "1.20.1"], ["fabric"], [first], ProjectId: "a"), new("a2", "two", "two", date, ["1.21.1"], ["fabric"], [second], ProjectId: "a", ReleaseChannel: "beta"), new("foreign", "bad", "bad", date.AddDays(1), ["26.3"], ["forge"], [first], ProjectId: "wrong")],
            ["b"] = [new("b1", "resource", "one", date, ["1.21.1"], [], [first], ProjectId: "b")]
        };
        CollectionAssert.AreEqual(new[] { "fabric" }, FavoriteDownloads.CommonLoaders([a, b], releases).ToArray()); CollectionAssert.AreEqual(new[] { "1.21.1" }, FavoriteDownloads.CommonVersions([a, b], releases, "fabric").ToArray());
        var plan = FavoriteDownloads.Plan([a, b], releases, "fabric", "1.21.1"); Assert.AreEqual(second, plan[0].File); Assert.AreNotEqual(plan[0].DestinationName, plan[1].DestinationName);
        Assert.Throws<InvalidDataException>(() => FavoriteDownloads.Plan([a, b], releases, "forge", "1.21.1")); Assert.Throws<InvalidDataException>(() => FavoriteDownloads.Plan([a, b], releases, "fabric", "1.20.1"));
    }
    [TestMethod]
    public async Task ExportReusesExactFilesPreservesConflictsAndContinuesOtherDownloads()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root); var source = Path.Combine(root, "cache"); Directory.CreateDirectory(source); var destination = Path.Combine(root, "out"); Directory.CreateDirectory(destination);
        var a = FileOf("same.jar", [1, 2]); var b = FileOf("occupied.jar", [3, 4]); var c = FileOf("new.jar", [5, 6]); await File.WriteAllBytesAsync(Path.Combine(destination, a.FileName), [1, 2]); await File.WriteAllBytesAsync(Path.Combine(destination, b.FileName), [9, 9]); var cache = Path.Combine(source, c.FileName); await File.WriteAllBytesAsync(cache, [5, 6]); var calls = 0;
        var result = await FavoriteDownloads.ExportAsync([new("a", "A", a, a.FileName), new("b", "B", b, b.FileName), new("c", "C", c, c.FileName)], destination + Path.DirectorySeparatorChar, (file, ct) => { calls++; return Task.FromResult(cache); }, concurrency: 3);
        Assert.AreEqual(1, calls); Assert.AreEqual(1, result.Added); Assert.AreEqual(1, result.Reused); Assert.AreEqual("B", result.Failures.Single().Name); CollectionAssert.AreEqual(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(Path.Combine(destination, b.FileName))); CollectionAssert.AreEqual(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(destination, c.FileName))); Assert.IsFalse(Directory.EnumerateFiles(destination, "*.partial").Any());
    }
    [TestMethod]
    public async Task BadCacheBytesAndTraversalCannotPublishAndCancellationLeavesNoPartialFiles()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(root); var output = Path.Combine(root, "out"); Directory.CreateDirectory(output); var cache = Path.Combine(root, "cache"); await File.WriteAllBytesAsync(cache, [9]); var file = FileOf("real.jar", [1, 2]);
        var result = await FavoriteDownloads.ExportAsync([new("a", "bad bytes", file, "real.jar"), new("b", "bad path", file, "../escape.jar")], output, (file, ct) => Task.FromResult(cache)); Assert.AreEqual(0, result.Added); Assert.AreEqual(2, result.Failures.Count); Assert.AreEqual(0, Directory.GetFiles(output).Length); Assert.IsFalse(File.Exists(Path.Combine(root, "escape.jar")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => FavoriteDownloads.ExportAsync([new("a", "cancel", file, file.FileName)], output, (f, ct) => Task.FromResult(cache), cancel.Token)); Assert.AreEqual(0, Directory.GetFiles(output).Length);
    }
}
