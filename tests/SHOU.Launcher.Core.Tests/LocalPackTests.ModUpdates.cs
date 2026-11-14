using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    [TestMethod]
    public async Task ImportedCurseForgeManifestResolvesItsHistoricalFileIdentityThroughTheCatalog()
    {
        var asset = Mod(); using var handler = new FixtureApi(asset); using var http = new HttpClient(handler);
        var reader = new LocalPackArchive(); var path = Archive(Manifest(asset)); var archive = await reader.ReadAsync(path);
        var resolver = new CurseForgePackResolver(http, FixtureKey); var resolution = await resolver.ResolveAsync(archive.CurseForgeFiles, "1.21.1", "fabric");
        var stage = Path.Combine(TestFiles.Root(), "stage");
        await reader.StageCurseForgeAsync(path, archive.Inspection.ArchiveSha256, stage, resolution.Files, (file, target, ct) => resolver.DownloadAsync(file, target, ct: ct));
        var manifest = JsonSerializer.Deserialize<ContentInstallManifest>(await File.ReadAllBytesAsync(Path.Combine(stage, ContentInstallFiles.ManifestName)))!;
        var old = manifest.Files.Single(); var query = LauncherService.NormalizeModOrigin(old);
        var release = await new CurseForgeCatalog(http, FixtureKey).ReleaseAsync(query.ReleaseId, default);
        Assert.AreEqual(old.ProjectId, release.ProjectId); Assert.AreEqual(old.Sha1, release.Files.Single().Sha1); Assert.AreEqual(old.Size, release.Files.Single().Size);
        Assert.AreEqual("cf-" + asset.FileId, old.ReleaseId, "Reading compatibility must not rewrite an existing pack receipt.");
    }
}
