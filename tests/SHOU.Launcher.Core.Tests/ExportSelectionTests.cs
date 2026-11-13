using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ExportSelectionTests
{
    [TestMethod, DataRow("cf"), DataRow("mrpack")]
    public async Task ExplicitDirectoriesAndSingleFilesRoundTripWithoutNetworkOrUnselectedContent(string format)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Local export stays offline.")));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        await using var service = new LauncherService(platform, http, downloader, new()); var instance = await service.CreateInstanceAsync("selection", "1.21.1");
        var files = new[] { "kubejs/server_scripts/a.js", "kubejs/config/account-token.txt", "scripts/one.zs", "scripts/not-selected.zs", "saves/World/fixture.txt", "custom.bin" };
        foreach (var relative in files) { var p = Path.Combine(instance.GameDirectory, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); await File.WriteAllTextAsync(p, "original: " + relative); }
        var rows = await service.GetInstanceExportEntriesAsync(instance.Id); Assert.IsTrue(rows.Single(r => r.RelativePath == "kubejs").Directory);
        var scripts = await service.GetInstanceExportEntriesAsync(instance.Id, "scripts"); Assert.HasCount(2, scripts); Assert.IsTrue(scripts.All(r => r.CanSelect));
        var options = new ModrinthPackExportOptions("Local selections", OverridePaths: ["kubejs", "scripts/one.zs", "saves/World", "custom.bin"]);
        var output = format == "cf" ? await service.ExportCurseForgePackAsync(instance.Id, options) : await service.ExportModrinthPackAsync(instance.Id, options);
        var included = files.Where(f => !f.EndsWith("not-selected.zs", StringComparison.Ordinal)).ToArray();
        using (var archive = ZipFile.OpenRead(output))
        {
            var prefix = format == "cf" ? "overrides/" : "client-overrides/";
            var payload = archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)).ToArray(); Assert.HasCount(included.Length, payload);
            foreach (var name in included) { using var reader = new StreamReader(archive.GetEntry(prefix + name)!.Open()); Assert.AreEqual("original: " + name, await reader.ReadToEndAsync()); }
        }
        var inspection = await service.InspectLocalPackAsync(output); Assert.IsTrue(inspection.CanImport);
        var imported = await service.ImportLocalPackAsync(new(output, inspection.ArchiveSha256, "Returned"));
        foreach (var name in included) Assert.AreEqual("original: " + name, await File.ReadAllTextAsync(Path.Combine(imported.Instance.GameDirectory, name)));
        Assert.IsFalse(File.Exists(Path.Combine(imported.Instance.GameDirectory, "scripts/not-selected.zs")));
        foreach (var name in files) Assert.AreEqual("original: " + name, await File.ReadAllTextAsync(Path.Combine(instance.GameDirectory, name)));
    }
    [TestMethod]
    public async Task BrowseRejectsTraversalLinksAndCancellationWhileMarkingRootCredentialsUnselectable()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Browse stays offline.")));
        var downloader = new VerifiedDownloader(http); var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader); await using var service = new LauncherService(platform, http, downloader, new()); var instance = await service.CreateInstanceAsync("browse", "1.21.1");
        await File.WriteAllTextAsync(Path.Combine(instance.GameDirectory, ".env"), "synthetic fixture");
        Directory.CreateSymbolicLink(Path.Combine(instance.GameDirectory, "alias"), TestFiles.Root());
        var rows = await service.GetInstanceExportEntriesAsync(instance.Id); Assert.IsFalse(rows.Single(r => r.RelativePath == ".env").CanSelect); Assert.IsFalse(rows.Single(r => r.RelativePath == "alias").CanSelect);
        await Assert.ThrowsAsync<IOException>(() => service.GetInstanceExportEntriesAsync(instance.Id, "alias"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetInstanceExportEntriesAsync(instance.Id, "../escape"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetInstanceExportEntriesAsync(instance.Id, cancellationToken: cancelled.Token));
        foreach (var extension in new[] { "png", "jpg", "jpeg", "bmp", "webp", "tif", "tiff" }) Assert.IsTrue(InstanceWorkspace.Accepts("screenshots", "image." + extension));
    }
}
