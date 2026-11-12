using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Catalog;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ResourceUpdateTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly byte[] Old = "original resource bytes"u8.ToArray(), Next = "updated resource bytes"u8.ToArray();
        internal string Root = "", Folder = "", Section = "", Kind = "", Mode = "";
        internal GameInstance Instance = null!; internal LauncherService Service = null!; internal HttpClient Http = null!;
        internal int Queries, Downloads; internal bool BadDownload, CancelDownload; internal Action? DuringDownload;
        internal string File => Path.Combine(Root, Folder, "My custom pack.zip");
        internal string Manifest => Path.Combine(Root, ContentInstallFiles.ManifestName);
        internal static async Task<Fixture> Create(string section, string mode = "", bool tracked = true)
        {
            var f = new Fixture { Section = section, Folder = section == "shaders" ? "shaderpacks" : section, Kind = section == "shaders" ? "shader" : "resourcepack", Mode = mode };
            f.Http = new(new ScriptedHttpHandler(f.Respond)); var downloader = new VerifiedDownloader(f.Http);
            var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader); f.Root = Path.Combine(platform.Paths.InstancesDirectory, "fixture"); Directory.CreateDirectory(Path.Combine(f.Root, f.Folder)); await System.IO.File.WriteAllBytesAsync(f.File, f.Old);
            f.Instance = new("fixture", "Fixture", "1.21.1", f.Root, Loader: "fabric", GameVersionId: "1.21.1");
            if (tracked) await System.IO.File.WriteAllBytesAsync(f.Manifest, JsonSerializer.SerializeToUtf8Bytes(new ContentInstallManifest(1, f.Instance.Id,
                [new("project", "old", f.Kind, f.Folder + "/My custom pack.zip", Hash(f.Old), Hash512(f.Old), f.Old.Length)])));
            await System.IO.File.WriteAllTextAsync(Path.Combine(f.Root, "options.txt"), "resourcePacks:[\"file/My custom pack.zip\"]\n");
            await System.IO.File.WriteAllTextAsync(Path.Combine(f.Root, "optionsshaders.txt"), "shaderPack=My custom pack.zip\n");
            await using (var store = new ConfigurationStore(platform.Paths)) await store.SaveAsync(new(new(), [f.Instance], []));
            f.Service = new(platform, f.Http, downloader, new NetworkOptions()); await f.Service.InitializeAsync(); return f;
        }
        private Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "cdn.modrinth.com")
            {
                Downloads++; DuringDownload?.Invoke(); if (CancelDownload) throw new OperationCanceledException();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BadDownload ? Old : Next) });
            }
            object Release(bool next) => new { id = next ? "next" : "old", project_id = next && Mode == "project" ? "wrong" : "project", name = "Resource", version_number = next ? "2" : "1", date_published = next ? "2026-11-02T00:00:00Z" : "2026-11-01T00:00:00Z", version_type = next && Mode == "beta" ? "beta" : "release", game_versions = new[] { next && Mode == "game" ? "1.20.1" : "1.21.1" }, loaders = new[] { "minecraft" }, dependencies = Array.Empty<object>(), files = new[] { new { filename = next ? "new-official-name.zip" : "old-official-name.zip", url = "https://cdn.modrinth.com/data/project/versions/" + (next ? "next/new-official-name.zip" : "old/old-official-name.zip"), size = (next ? Next : Old).Length + (!next && Mode == "size" ? 1 : 0), primary = true, hashes = new { sha1 = Hash(next && Mode != "same" ? Next : Old), sha512 = Hash512(next ? Next : Old) } } } };
            object body;
            if (path.EndsWith("/version_files")) body = new Dictionary<string, object> { [Hash(Old)] = Release(false) };
            else if (path.Contains("/version_file/")) body = Release(false);
            else if (path.EndsWith("/project/project/version"))
            { Queries++; Assert.IsFalse(request.RequestUri.Query.Contains("loaders", StringComparison.Ordinal)); body = new[] { Release(true) }; }
            else if (path.EndsWith("/project/project")) body = new { id = "project", slug = "project", title = "Verified resource", description = "Description", project_type = Mode == "kind" ? "mod" : Kind, downloads = 1 };
            else if (path.EndsWith("/version/next")) body = Release(true);
            else return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(body)));
        }
        internal Task<InstanceModUpdateCheck> Check(bool refresh = false) => Service.CheckInstanceResourceUpdatesAsync(Instance.Id, Section, refresh);
        internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
        private static string Hash512(byte[] bytes) => Convert.ToHexStringLower(SHA512.HashData(bytes));
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Http.Dispose(); }
    }
    [TestMethod]
    [DataRow("resourcepack", 12)]
    [DataRow("shader", 6552)]
    public async Task CurseForgeResourcesRetainTypedIdentityAndQueryWithoutAModLoader(string kind, int classId)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath; object data;
            if (path.EndsWith("/mods/123")) data = new { id = 123, gameId = 432, classId, slug = "fixture", name = "Fixture", links = new { websiteUrl = "https://www.curseforge.com/minecraft/texture-packs/fixture" } };
            else
            {
                Assert.IsFalse(request.RequestUri.Query.Contains("modLoaderType", StringComparison.Ordinal));
                data = new[] { new { id = 456, modId = 123, gameId = 432, fileName = "fixture.zip", displayName = "Fixture 2", fileLength = 10, downloadUrl = "https://edge.forgecdn.net/files/0/456/fixture.zip", releaseType = 1, fileDate = "2026-11-02T00:00:00Z", gameVersions = new[] { "1.21.1" }, hashes = new[] { new { algo = 1, value = new string('a', 40) } } } };
            }
            return Task.FromResult(ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { data })));
        }));
        var catalog = new CurseForgeCatalog(http, "fixture-key"); var project = await catalog.ProjectAsync("cf-123", default); Assert.AreEqual(kind, project.Kind);
        var release = (await catalog.ReleasesAsync(project.Id, "1.21.1", null, default)).Single(); Assert.AreEqual("cf-123", release.ProjectId); Assert.IsTrue(ContentInstallPlanner.Compatible(release, kind, "1.21.1", "fabric")); Assert.AreEqual("fixture.zip", release.Files.Single().FileName);
    }
    [TestMethod]
    public async Task UnsupportedSectionAndExplicitCancellationDoNotStartAnUpdate()
    {
        await using var f = await Fixture.Create("resourcepacks");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CheckInstanceResourceUpdatesAsync(f.Instance.Id, "mods"));
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.CheckInstanceResourceUpdatesAsync(f.Instance.Id, "resourcepacks", cancellationToken: source.Token));
        Assert.AreEqual(0, f.Downloads); CollectionAssert.AreEqual(f.Old, await File.ReadAllBytesAsync(f.File));
    }
    [TestMethod]
    [DataRow("resourcepacks")]
    [DataRow("shaders")]
    public async Task ExactIdentityUpdatesCacheRefreshBackupAndPreserveInstalledNameAndGameSelections(string section)
    {
        await using var f = await Fixture.Create(section); var manifest = await File.ReadAllBytesAsync(f.Manifest); var options = await File.ReadAllBytesAsync(Path.Combine(f.Root, "options.txt")); var shaderOptions = await File.ReadAllBytesAsync(Path.Combine(f.Root, "optionsshaders.txt"));
        var first = await f.Check(); Assert.HasCount(1, first.Updates); Assert.AreEqual("1", first.OnlineInfo!.Single().Version); Assert.AreEqual(1, f.Queries);
        var cached = await f.Check(); Assert.HasCount(1, cached.Updates); Assert.AreEqual(1, f.Queries);
        var refreshed = await f.Check(true); Assert.AreEqual(2, f.Queries); var update = refreshed.Updates.Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyInstanceResourceUpdateAsync(first.Updates[0].Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyInstanceModUpdateAsync(update.Token));
        var result = await f.Service.ApplyInstanceResourceUpdateAsync(update.Token); Assert.AreEqual(1, f.Downloads); CollectionAssert.AreEqual(f.Next, await File.ReadAllBytesAsync(f.File));
        CollectionAssert.AreEqual(f.Old, await File.ReadAllBytesAsync(Path.Combine(result.BackupDirectory, update.RelativePath))); CollectionAssert.AreEqual(manifest, await File.ReadAllBytesAsync(Path.Combine(result.BackupDirectory, ContentInstallFiles.ManifestName)));
        var installed = (await ContentInstallFiles.ReadManifestAsync(f.Instance)).Files.Single(); Assert.AreEqual(f.Kind, installed.Kind); Assert.AreEqual("next", installed.ReleaseId); Assert.AreEqual(update.RelativePath, installed.RelativePath);
        CollectionAssert.AreEqual(options, await File.ReadAllBytesAsync(Path.Combine(f.Root, "options.txt"))); CollectionAssert.AreEqual(shaderOptions, await File.ReadAllBytesAsync(Path.Combine(f.Root, "optionsshaders.txt"))); Assert.HasCount(1, Directory.GetFiles(Path.Combine(f.Root, f.Folder)));
    }
    [TestMethod]
    [DataRow("beta")]
    [DataRow("game")]
    [DataRow("project")]
    [DataRow("kind")]
    [DataRow("size")]
    [DataRow("same")]
    public async Task IneligibleOrUnverifiedFilesNeverBecomeUpdatesAndCheckingDoesNotCreateAManifest(string mode)
    {
        await using var f = await Fixture.Create("resourcepacks", mode, false); var result = await f.Check(); Assert.IsEmpty(result.Updates); Assert.AreEqual(0, f.Downloads);
        if (mode is "kind" or "size") Assert.IsNotEmpty(result.Notices);
        CollectionAssert.AreEqual(f.Old, await File.ReadAllBytesAsync(f.File)); Assert.IsFalse(File.Exists(f.Manifest));
    }
    [TestMethod]
    [DataRow("hash")]
    [DataRow("cancel")]
    [DataRow("file")]
    [DataRow("manifest")]
    public async Task FailedTransferOrExternalChangesPreserveOriginalState(string mode)
    {
        await using var f = await Fixture.Create("shaders"); var originalManifest = await File.ReadAllBytesAsync(f.Manifest); var update = (await f.Check()).Updates.Single();
        f.BadDownload = mode == "hash"; f.CancelDownload = mode == "cancel";
        if (mode == "file") f.DuringDownload = () => File.WriteAllText(f.File, "external file");
        if (mode == "manifest") f.DuringDownload = () => File.WriteAllText(f.Manifest, "external manifest");
        try { await f.Service.ApplyInstanceResourceUpdateAsync(update.Token); Assert.Fail("The failed transaction must not succeed."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or AggregateException) { }
        if (mode == "file") Assert.AreEqual("external file", await File.ReadAllTextAsync(f.File)); else CollectionAssert.AreEqual(f.Old, await File.ReadAllBytesAsync(f.File));
        if (mode == "manifest") Assert.AreEqual("external manifest", await File.ReadAllTextAsync(f.Manifest)); else CollectionAssert.AreEqual(originalManifest, await File.ReadAllBytesAsync(f.Manifest));
        Assert.IsEmpty(Directory.GetDirectories(f.Root, ".shou-resource-update-stage-*"));
    }
    [TestMethod]
    [DataRow("resourcepacks")]
    [DataRow("shaders")]
    public async Task CancellationAfterPublishingRollsBackExactResourceAndManifest(string section)
    {
        await using var f = await Fixture.Create(section); var old = (await ContentInstallFiles.ReadManifestAsync(f.Instance)).Files.Single(); var manifest = await File.ReadAllBytesAsync(f.Manifest); var validations = 0;
        var file = new ContentFile("new-official-name.zip", new("https://cdn.modrinth.com/data/project/versions/next/new-official-name.zip"), Fixture.Hash(f.Next), f.Next.Length, true);
        var plan = new ContentInstallPlan(f.Instance.Id, "1.21.1", "fabric", [new("project", "next", f.Kind, old.RelativePath, file, false)], []);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModUpdateFiles.ApplyAsync(f.Instance, old, plan, (_, p, ct) => File.WriteAllBytesAsync(p, f.Next, ct), () => { if (++validations == 3) throw new OperationCanceledException(); }));
        CollectionAssert.AreEqual(f.Old, await File.ReadAllBytesAsync(f.File)); CollectionAssert.AreEqual(manifest, await File.ReadAllBytesAsync(f.Manifest));
    }
}
