using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class LocalPackTests
{
    private static JsonObject OptionalManifest(Asset required, Asset optional)
    {
        var metadata = Manifest(required, optional); metadata["files"]![1]!["required"] = false; return metadata;
    }
    [TestMethod]
    public async Task ReplacingArchiveCannotCarryOldOptionalKeysIntoTheNewManifest()
    {
        var mod=Mod();var old=Resource();var newer=old with{Project=103,FileId=1003,Name="new-optional.zip"};
        using var handler=new FixtureApi(mod,old,newer);using var http=new HttpClient(handler);var downloader=new VerifiedDownloader(http);
        var platform=PlatformServicesFactory.Create(new(TestFiles.Root()),downloader) with{Credentials=new EmptyCredentials()};
        using var lease=platform.DataLock.Acquire(platform.Paths);await using var processes=(ProcessRunner)platform.Processes;
        await using var service=new LauncherService(platform,http,downloader,new NetworkOptions(),localPackResolver:new(http,FixtureKey));
        var path=Archive(OptionalManifest(mod,old));var initial=await service.InspectLocalPackAsync(path);var key=initial.OptionalFiles!.Single().Key;
        await File.WriteAllBytesAsync(path,Zip(("manifest.json",OptionalManifest(mod,newer).ToJsonString())));
        var fresh=await service.InspectLocalPackSelectionAsync(path,[key],expectedArchiveSha256:initial.ArchiveSha256);
        Assert.AreNotEqual(initial.ArchiveSha256,fresh.ArchiveSha256);Assert.IsEmpty(fresh.SelectedOptionalFiles!);
        Assert.AreEqual(CurseForgePackSelection.Key(new(newer.Project,newer.FileId,false)),fresh.OptionalFiles!.Single().Key);
        await Assert.ThrowsAsync<IOException>(()=>service.ImportLocalPackAsync(new(path,initial.ArchiveSha256,"Old approval",[key])));Assert.IsEmpty(service.Snapshot.Instances);
    }
    [TestMethod]
    public async Task OptionalChoicesUseOfficialNamesAndSelectionSpecificPlansNeverInstallAnUnselectedFile()
    {
        var mod = Mod(); var optional = Resource(); using var handler = new FixtureApi(mod, optional); using var http = new HttpClient(handler);
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        var resolver = new CurseForgePackResolver(http, FixtureKey, platform.Paths.CacheDirectory);
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), localPackResolver: resolver);
        var path = Archive(OptionalManifest(mod, optional)); var normal = await service.InspectLocalPackAsync(path);
        var choice = normal.OptionalFiles!.Single(); Assert.AreEqual(optional.Name, choice.Name); Assert.IsTrue(normal.CanImport);
        Assert.IsEmpty(normal.SelectedOptionalFiles!); Assert.AreEqual(1, normal.LocalFiles!.Count);
        var selected = await service.InspectLocalPackSelectionAsync(path, [choice.Key]); Assert.IsTrue(selected.CanImport);
        Assert.AreEqual(2, selected.LocalFiles!.Count);
        // The most recently cached plan contains the optional file, but an empty selection must still omit it.
        var unselected = await service.ImportLocalPackAsync(new(path, normal.ArchiveSha256, "Required only"));
        Assert.IsFalse(File.Exists(Path.Combine(unselected.Instance.GameDirectory, "resourcepacks/fixture.zip")));
        var included = await service.ImportLocalPackAsync(new(path, normal.ArchiveSha256, "Selected", [choice.Key]));
        CollectionAssert.AreEqual(optional.Bytes, await File.ReadAllBytesAsync(Path.Combine(included.Instance.GameDirectory, "resourcepacks/fixture.zip")));
        var manifest = await ContentInstallFiles.ReadManifestAsync(included.Instance); Assert.AreEqual("optional", manifest.Files.Single(f => f.Kind == "resourcepack").Client);
        var plan = await CurseForgePackExport.PlanAsync(included.Instance, new("Roundtrip", BundleInstalledFiles: true), default);
        Assert.IsFalse(plan.References.Single(f => f.FileId == optional.FileId).Required);
        var export = await ShouPackArchive.ExportAsync(included.Instance, Path.Combine(TestFiles.Root(), "exports"), false, default, plan);
        var exported = await new LocalPackArchive().ReadAsync(export); Assert.IsFalse(exported.CurseForgeFiles.Single(f => f.FileId == optional.FileId).Required);
        Assert.IsTrue(service.Snapshot.Instances.All(i => !i.Installed));
    }
    [TestMethod]
    public async Task UnavailableUnselectedMetadataDoesNotBlockRequiredFilesButChoosingItDoes()
    {
        var mod = Mod(); var optional = Resource(); using var handler = new FixtureApi(mod, optional); using var http = new HttpClient(handler);
        handler.Override = r => r.RequestUri!.AbsolutePath.EndsWith($"/mods/{optional.Project}/files/{optional.FileId}", StringComparison.Ordinal) ? new(HttpStatusCode.ServiceUnavailable) : null;
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), localPackResolver: new(http, FixtureKey, platform.Paths.CacheDirectory));
        var path = Archive(OptionalManifest(mod, optional)); var initial = await service.InspectLocalPackAsync(path); Assert.IsTrue(initial.CanImport);
        var choice = initial.OptionalFiles!.Single(); Assert.IsNotNull(choice.Notice);
        var selected = await service.InspectLocalPackSelectionAsync(path, [choice.Key]); Assert.IsFalse(selected.CanImport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportLocalPackAsync(new(path, initial.ArchiveSha256, "Blocked", [choice.Key])));
        Assert.IsEmpty(service.Snapshot.Instances);
    }
    [TestMethod]
    public async Task SelectedOptionalManualFileCanBeProvidedAndDeselectingItStillSkipsCachedBytes()
    {
        var required = Resource(); var optional = Mod(true); using var handler = new FixtureApi(required, optional); using var http = new HttpClient(handler);
        handler.Override = r => r.RequestUri!.Host == "api.modrinth.com" ? new(HttpStatusCode.NotFound) : null;
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), localPackResolver: new(http, FixtureKey, platform.Paths.CacheDirectory));
        var path = Archive(OptionalManifest(required, optional)); var initial = await service.InspectLocalPackAsync(path); var key = initial.OptionalFiles!.Single().Key;
        Assert.IsTrue(initial.CanImport); var selected = await service.InspectLocalPackSelectionAsync(path, [key]); Assert.IsFalse(selected.CanImport);
        Assert.AreEqual(optional.FileId, selected.ManualFiles!.Single().FileId);
        var supplied = Path.Combine(TestFiles.Root(), "provided.jar"); await File.WriteAllTextAsync(supplied, "wrong bytes");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ProvideSelectedLocalPackFileAsync(path, initial.ArchiveSha256, [key], optional.Project, optional.FileId, supplied));
        await File.WriteAllBytesAsync(supplied, optional.Bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ProvideLocalPackFileAsync(path, initial.ArchiveSha256, optional.Project, optional.FileId, supplied));
        await service.ProvideSelectedLocalPackFileAsync(path, initial.ArchiveSha256, [key], optional.Project, optional.FileId, supplied);
        Assert.IsTrue((await service.InspectLocalPackSelectionAsync(path, [key])).CanImport);
        var installed = await service.ImportLocalPackAsync(new(path, initial.ArchiveSha256, "Selected", [key]));
        CollectionAssert.AreEqual(optional.Bytes, await File.ReadAllBytesAsync(Path.Combine(installed.Instance.GameDirectory, "mods/fixture.jar")));
        var skipped = await service.ImportLocalPackAsync(new(path, initial.ArchiveSha256, "Skipped"));
        Assert.IsFalse(File.Exists(Path.Combine(skipped.Instance.GameDirectory, "mods/fixture.jar")));
    }
    [TestMethod, DataRow("unknown"), DataRow("required"), DataRow("duplicate")]
    public async Task OptionalSelectionRejectsUnknownRequiredAndDuplicateKeysBeforeRequests(string scenario)
    {
        var mod = Mod(); var optional = Resource(); using var handler = new FixtureApi(mod, optional); using var http = new HttpClient(handler);
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), localPackResolver: new(http, FixtureKey));
        var source = Archive(OptionalManifest(mod, optional)); var sha = (await new LocalPackArchive().ReadAsync(source)).Inspection.ArchiveSha256;
        var key = CurseForgePackSelection.Key(new(optional.Project, optional.FileId, false));
        string[] selected = scenario == "unknown" ? ["cf:999:999"] : scenario == "required" ? [CurseForgePackSelection.Key(new(mod.Project, mod.FileId, true))] : [key, key];
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectLocalPackSelectionAsync(source, selected));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportLocalPackAsync(new(source, sha, "Invalid", selected)));
        Assert.IsEmpty(handler.Requests); Assert.IsEmpty(service.Snapshot.Instances);
    }
}
