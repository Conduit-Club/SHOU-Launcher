using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ModUpdateFilesTests
{
    private sealed class Fixture
    {
        internal readonly byte[] OldBytes = "original mod bytes"u8.ToArray(), NewBytes = "updated mod bytes"u8.ToArray();
        internal readonly string Root = TestFiles.Root();
        internal GameInstance Instance;
        internal ContentInstalledFile Old;
        internal ContentInstallPlan Plan;
        internal byte[] Manifest;
        internal string OldPath => Path.Combine(Root, Old.RelativePath);
        internal string ManifestPath => Path.Combine(Root, ContentInstallFiles.ManifestName);
        internal Fixture(bool disabled = false, bool sameName = false)
        {
            Instance = new("fixture", "Fixture", "1.21.1", Root);
            var path = "mods/old.jar" + (disabled ? ".disabled" : ""); Directory.CreateDirectory(Path.Combine(Root, "mods")); File.WriteAllBytes(Path.Combine(Root, path), OldBytes);
            Old = new("project", "oldversion", "mod", path, Convert.ToHexStringLower(SHA1.HashData(OldBytes)), Convert.ToHexStringLower(SHA512.HashData(OldBytes)), OldBytes.Length);
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new ContentInstallManifest(1, Instance.Id, [Old])); File.WriteAllBytes(ManifestPath, Manifest);
            var name = sameName ? "old.jar" : "new.jar";
            var file = new ContentFile(name, new("https://cdn.modrinth.com/data/project/versions/newversion/" + name), Convert.ToHexStringLower(SHA1.HashData(NewBytes)), NewBytes.Length, true);
            Plan = new(Instance.Id, "1.21.1", "fabric", [new("project", "newversion", "mod", "mods/" + name, file, false)], []);
        }
        internal Task Download(ContentFile file, string path, CancellationToken ct) => File.WriteAllBytesAsync(path, NewBytes, ct);
        internal void AssertOriginal()
        {
            CollectionAssert.AreEqual(OldBytes, File.ReadAllBytes(OldPath)); CollectionAssert.AreEqual(Manifest, File.ReadAllBytes(ManifestPath));
            Assert.IsFalse(Directory.EnumerateDirectories(Root, ".shou-mod-update-stage-*").Any());
        }
    }
    [TestMethod]
    public async Task SuccessfulUpdateBacksUpOriginalJarAndExactManifest()
    {
        var f = new Fixture(); var result = await ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, f.Download, () => { });
        Assert.IsFalse(File.Exists(f.OldPath)); CollectionAssert.AreEqual(f.NewBytes, File.ReadAllBytes(Path.Combine(f.Root, "mods/new.jar")));
        CollectionAssert.AreEqual(f.OldBytes, File.ReadAllBytes(Path.Combine(result.BackupDirectory, f.Old.RelativePath)));
        CollectionAssert.AreEqual(f.Manifest, File.ReadAllBytes(Path.Combine(result.BackupDirectory, ContentInstallFiles.ManifestName)));
        var installed = await ContentInstallFiles.ReadManifestAsync(f.Instance); Assert.HasCount(1, installed.Files); Assert.AreEqual("newversion", installed.Files[0].ReleaseId);
    }
    [TestMethod]
    public async Task SameNameDisabledUpdateStaysDisabledAndRetainsOriginal()
    {
        var f = new Fixture(true, true); var result = await ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, f.Download, () => { });
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, "mods/old.jar"))); CollectionAssert.AreEqual(f.NewBytes, File.ReadAllBytes(f.OldPath));
        CollectionAssert.AreEqual(f.OldBytes, File.ReadAllBytes(Path.Combine(result.BackupDirectory, f.Old.RelativePath)));
    }
    [TestMethod]
    public async Task HashFailureAndDownloadCancellationPreserveOriginal()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, (_, path, ct) => File.WriteAllTextAsync(path, "bad", ct), () => { })); f.AssertOriginal();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, (_, _, _) => throw new OperationCanceledException(), () => { })); f.AssertOriginal();
    }
    [TestMethod]
    public async Task CancellationAfterPublishingRestoresOriginalAndManifest()
    {
        var f = new Fixture(); var validations = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, f.Download, () => { if (++validations == 3) throw new OperationCanceledException(); }));
        f.AssertOriginal(); Assert.IsFalse(File.Exists(Path.Combine(f.Root, "mods/new.jar")));
    }
    [TestMethod]
    public async Task ChangedOriginalOrManifestDuringDownloadIsNotOverwritten()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, async (file, path, ct) => { await f.Download(file, path, ct); await File.WriteAllTextAsync(f.OldPath, "external change", ct); }, () => { }));
        Assert.AreEqual("external change", File.ReadAllText(f.OldPath)); CollectionAssert.AreEqual(f.Manifest, File.ReadAllBytes(f.ManifestPath));
        f = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, async (file, path, ct) => { await f.Download(file, path, ct); await File.WriteAllTextAsync(f.ManifestPath, "external manifest", ct); }, () => { }));
        Assert.AreEqual("external manifest", File.ReadAllText(f.ManifestPath)); CollectionAssert.AreEqual(f.OldBytes, File.ReadAllBytes(f.OldPath));
    }
    [TestMethod]
    public async Task ConflictingTargetStopsBeforeDownload()
    {
        var f = new Fixture(); File.WriteAllText(Path.Combine(f.Root, "mods/new.jar"), "unrelated");
        await Assert.ThrowsAsync<IOException>(() => ModUpdateFiles.ApplyAsync(f.Instance, f.Old, f.Plan, (_, _, _) => throw new AssertFailedException("must not download"), () => { })); f.AssertOriginal();
    }
}
