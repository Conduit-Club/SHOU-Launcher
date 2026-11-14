using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed partial class WorldContentInstallTests
{
    [TestMethod]
    public async Task ProjectOnlyDualModDatapackDependencyQueriesDatapackVariantForVanillaWorld()
    {
        await using var f = await Fixture.Create(); f.Catalog.Add("api", f.Pack("api", "api")); f.Catalog.ModrinthDatapackRepresentation("api");
        f.Catalog.Add("main", f.Pack("main", "main"), dependencies: [new("required", "api")]);
        var plan = await new ContentInstallPlanner(f.Catalog, f.Catalog).PlanAsync(f.Instance with { GameDirectory = f.World }, f.Version,
            new(f.Instance.Id, "main", "main", "main.zip", "datapack"), worldDatapacks: true);
        Assert.AreEqual("datapack", f.Catalog.LastLoader); Assert.HasCount(2, plan.Files);
        Assert.IsTrue(plan.Files.All(f => f.Kind == "datapack" && f.RelativePath.StartsWith("datapacks/")));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task DatapackDownloadPreviewsDependenciesThenExplicitlyInstallsIntoChosenWorldAndReuses(bool modern)
    {
        await using var f = await Fixture.Create(modern);
        f.Catalog.Add("api", f.Pack("api", "api"));
        f.Catalog.Add("main", f.Pack("main", "main"), dependencies: [new("required", "api", "api")]);
        f.Catalog.ModrinthDatapackRepresentation("main");
        var level = await File.ReadAllBytesAsync(f.Level);
        var request = f.Request("main"); var preview = await f.Service.PreviewWorldContentAsync(request);
        Assert.HasCount(2, preview.Files); Assert.IsFalse(Directory.Exists(Path.Combine(f.World, "datapacks")));
        var result = await f.Service.InstallWorldContentAsync(preview.Id); Assert.AreEqual(2, result.AddedFiles);
        Assert.IsTrue(File.Exists(Path.Combine(f.World, "datapacks/main.zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(f.OtherWorld, "datapacks")));
        CollectionAssert.AreEqual(level, await File.ReadAllBytesAsync(f.Level));
        var installed = await ContentInstallFiles.ReadManifestAsync(f.Instance with { GameDirectory = f.World }); Assert.HasCount(2, installed.Files);
        var repeat = await f.Service.InstallWorldContentAsync((await f.Service.PreviewWorldContentAsync(request)).Id); Assert.AreEqual(0, repeat.AddedFiles);
        Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-content-stage-*").Any());
        Assert.AreEqual(f.Instance.Id, f.Service.Snapshot.SelectedInstanceId);
    }
    [TestMethod, DataRow("format"), DataRow("crc"), DataRow("traversal"), DataRow("features"), DataRow("file-conflict"), DataRow("resource-conflict"), DataRow("unicode"), DataRow("required-mod")]
    public async Task DatapackPreflightRejectsUnsafeIncompatibleAndConflictingResourcesWithoutWorldChanges(string scenario)
    {
        await using var f = await Fixture.Create(); var archive = f.Pack("shared", "candidate");
        if (scenario == "format") archive = Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":99}}", ["data/test/function/shared.mcfunction"] = "say value" });
        if (scenario == "features") archive = Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":48},\"features\":{\"enabled\":[\"test:flag\"]}}" });
        if (scenario == "traversal") archive = Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":48}}", ["../outside.txt"] = "no" });
        if (scenario == "crc") { archive = archive.ToArray(); for (var i = 0; i < archive.Length - 46; i++) if (archive[i] == 0x50 && archive[i + 1] == 0x4b && archive[i + 2] == 1 && archive[i + 3] == 2) { archive[i + 16] ^= 0xff; break; } }
        if (scenario is "file-conflict" or "resource-conflict" or "unicode")
        {
            Directory.CreateDirectory(Path.Combine(f.World, "datapacks"));
            await File.WriteAllBytesAsync(Path.Combine(f.World, "datapacks", scenario == "file-conflict" ? "main.zip" : scenario == "unicode" ? "MAIN.zip" : "existing.zip"), f.Pack("shared", "existing"));
        }
        f.Catalog.Add("main", archive, dependencies: scenario == "required-mod" ? [new("required", "mod", "mod")] : []);
        if (scenario == "required-mod") f.Catalog.Add("mod", archive, kind: "mod");
        var before = Files(f.World); await Reject(() => f.Service.PreviewWorldContentAsync(f.Request("main")));
        CollectionAssert.AreEqual(before, Files(f.World)); Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-content-stage-*").Any());
    }
    [TestMethod, DataRow("level"), DataRow("pack"), DataRow("cache"), DataRow("cancel")]
    public async Task ConfirmRechecksPreviewAndCancelledOrStaleInstallationLeavesTargetUntouched(string scenario)
    {
        await using var f = await Fixture.Create(); f.Catalog.Add("main", f.Pack("main", "value"));
        var preview = await f.Service.PreviewWorldContentAsync(f.Request("main"));
        if (scenario == "level") await File.WriteAllBytesAsync(f.Level, Level(f.Version, f.DataVersion, "external edit"));
        if (scenario == "pack") { Directory.CreateDirectory(Path.Combine(f.World, "datapacks")); await File.WriteAllBytesAsync(Path.Combine(f.World, "datapacks/new.zip"), f.Pack("main", "external")); }
        if (scenario == "cache") { var file = f.Catalog.Files["main"]; await File.WriteAllTextAsync(Path.Combine(f.Platform.Paths.CacheDirectory, "content", file.Sha1, "main.zip"), "changed cached archive"); }
        var before = Files(f.World); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        await Reject(() => f.Service.InstallWorldContentAsync(preview.Id, cts.Token)); CollectionAssert.AreEqual(before, Files(f.World));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task NetworkFailureAndCancellationDuringArchiveTransferLeaveWorldAndNoPartialDownload(bool cancel)
    {
        await using var f = await Fixture.Create(); f.Catalog.Add("main", f.Pack("main", "value"));
        using var cts = new CancellationTokenSource(); f.Handler.BeforeArchive = () => { if (cancel) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); } throw new IOException("controlled transfer failure"); };
        var before = Files(f.World); await Reject(() => f.Service.PreviewWorldContentAsync(f.Request("main"), cts.Token));
        CollectionAssert.AreEqual(before, Files(f.World)); Assert.IsFalse(Directory.EnumerateFiles(f.Platform.Paths.CacheDirectory, "*.part", SearchOption.AllDirectories).Any());
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task WorldZipInstallsOnlyAfterConfirmationIntoNewNamedFolderPreservingNbtAndRegion(bool wrapped)
    {
        await using var f = await Fixture.Create(); var level = Level(f.Version, f.DataVersion, "下载世界");
        var prefix = wrapped ? "地图 wrapper/" : "";
        var payload = new Dictionary<string, byte[]> { [prefix + "level.dat"] = level, [prefix + "region/r.0.0.mca"] = Encoding.UTF8.GetBytes("controlled region"), [prefix + "session.lock"] = [1, 2, 3] };
        f.Catalog.Add("map", ZipBytes(payload), kind: "world");
        var preview = await f.Service.PreviewWorldContentAsync(f.Request("map", "world", "新世界 中文 空格"));
        var destination = Path.Combine(f.Instance.GameDirectory, "saves/新世界 中文 空格"); Assert.IsFalse(Directory.Exists(destination));
        await f.Service.InstallWorldContentAsync(preview.Id);
        CollectionAssert.AreEqual(level, await File.ReadAllBytesAsync(Path.Combine(destination, "level.dat")));
        Assert.AreEqual("controlled region", await File.ReadAllTextAsync(Path.Combine(destination, "region/r.0.0.mca")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "session.lock"))); Assert.IsTrue(File.Exists(Path.Combine(destination, ".shou-world-source.json")));
        Assert.IsTrue((await f.Service.GetWorldContentTargetsAsync(f.Instance.Id)).Any(w => w.DirectoryName == "新世界 中文 空格"));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
    }
    [TestMethod, DataRow("version"), DataRow("multiple"), DataRow("script"), DataRow("outside"), DataRow("target"), DataRow("ziplink")]
    public async Task InvalidWorldAndDestinationConflictsNeverOverwriteExistingWorlds(string scenario)
    {
        await using var f = await Fixture.Create(); var entries = new Dictionary<string, byte[]> { ["level.dat"] = Level(f.Version, scenario == "version" ? 999999 : f.DataVersion, "download") };
        if (scenario == "multiple") entries["other/level.dat"] = entries["level.dat"];
        if (scenario == "script") entries["install.ps1"] = [1];
        if (scenario == "outside") { entries["wrapper/level.dat"] = entries["level.dat"]; entries.Remove("level.dat"); entries["readme.txt"] = [1]; }
        var bytes = ZipBytes(entries);
        if (scenario == "ziplink")
        {
            using var ms = new MemoryStream(); using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { var e = zip.CreateEntry("level.dat"); e.ExternalAttributes = unchecked((int)(0xa000u << 16)); using var output = e.Open(); output.Write(entries["level.dat"]); } bytes = ms.ToArray();
        }
        f.Catalog.Add("map", bytes, kind: "world"); var before = Files(f.Instance.GameDirectory);
        await Reject(() => f.Service.PreviewWorldContentAsync(f.Request("map", "world", scenario == "target" ? "现有世界" : "new")));
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory));
    }
    [TestMethod, DataRow("cancel"), DataRow("cache"), DataRow("appeared")]
    public async Task WorldCommitFailureAndCancellationKeepOldWorldsAndRemoveOwnedStaging(string scenario)
    {
        await using var f = await Fixture.Create(); f.Catalog.Add("map", ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "download") }), kind: "world");
        var preview = await f.Service.PreviewWorldContentAsync(f.Request("map", "world", "new"));
        if (scenario == "cache") { var file = f.Catalog.Files["map"]; await File.WriteAllTextAsync(Path.Combine(f.Platform.Paths.CacheDirectory, "content", file.Sha1, "map.zip"), "bad"); }
        if (scenario == "appeared") { Directory.CreateDirectory(Path.Combine(f.Instance.GameDirectory, "saves/new")); await File.WriteAllTextAsync(Path.Combine(f.Instance.GameDirectory, "saves/new/keep.txt"), "external"); }
        var before = Files(f.Instance.GameDirectory); using var cts = new CancellationTokenSource(); if (scenario == "cancel") cts.Cancel();
        await Reject(() => f.Service.InstallWorldContentAsync(preview.Id, cts.Token)); CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task DatapackFailureAtFinalCommitRollsBackPublishedFilesAndNewDirectory(bool cancel)
    {
        await using var f = await Fixture.Create(); f.Catalog.Add("main", f.Pack("main", "value")); var file = f.Catalog.Files["main"];
        var plan = new ContentInstallPlan(f.Instance.Id, f.Version, "vanilla", [new("main", "main", "datapack", "datapacks/main.zip", file, false)], []);
        var before = Files(f.World); using var cts = new CancellationTokenSource(); var reached = false;
        await Reject(() => ContentInstallFiles.InstallAsync(f.Instance with { GameDirectory = f.World }, plan,
            (input, target, token) => File.WriteAllBytesAsync(target, f.Handler.Bytes[input.Uri.AbsoluteUri], token), cts.Token,
            (published, token) => { if (!published) return Task.CompletedTask; reached = true; if (cancel) { cts.Cancel(); token.ThrowIfCancellationRequested(); } throw new IOException("controlled final commit failure"); }));
        Assert.IsTrue(reached); CollectionAssert.AreEqual(before, Files(f.World)); Assert.IsFalse(Directory.Exists(Path.Combine(f.World, "datapacks")));
        Assert.IsFalse(Directory.EnumerateDirectories(f.World, ".shou-content-stage-*").Any());
    }
    [TestMethod]
    public async Task CorruptLaterWorldEntryRollsBackExtractedStageAndPreservesOldWorlds()
    {
        await using var f = await Fixture.Create();
        var archive = ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "download"), ["region/r.0.0.mca"] = Encoding.UTF8.GetBytes("controlled region") });
        var seen = 0; for (var i = 0; i < archive.Length - 46; i++) if (archive[i] == 0x50 && archive[i + 1] == 0x4b && archive[i + 2] == 1 && archive[i + 3] == 2 && ++seen == 2) { archive[i + 16] ^= 0xff; break; }
        f.Catalog.Add("map", archive, kind: "world"); var preview = await f.Service.PreviewWorldContentAsync(f.Request("map", "world", "new"));
        var before = Files(f.Instance.GameDirectory); await Reject(() => f.Service.InstallWorldContentAsync(preview.Id));
        CollectionAssert.AreEqual(before, Files(f.Instance.GameDirectory)); Assert.IsFalse(Directory.Exists(Path.Combine(f.Instance.GameDirectory, "saves/new")));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Instance.GameDirectory, ".shou-world-install-*").Any());
    }
    [TestMethod, DataRow("supported", false), DataRow("minor-max", true), DataRow("minor-min", true), DataRow("tags", false), DataRow("tags-replace", false), DataRow("overlay-inactive", false), DataRow("overlay-active", false)]
    public async Task FormatRangesTagMergingAndActiveOverlaysControlCompatibility(string scenario, bool modern)
    {
        await using var f = await Fixture.Create(modern);
        var metadata = scenario == "supported" ? "{\"pack\":{\"pack_format\":48,\"supported_formats\":[40,48]}}"
            : scenario == "minor-max" ? "{\"pack\":{\"min_format\":107,\"max_format\":[107,0]}}"
            : scenario == "minor-min" ? "{\"pack\":{\"min_format\":[107,2],\"max_format\":107}}"
            : scenario.StartsWith("overlay") ? "{\"pack\":{\"pack_format\":48},\"overlays\":{\"entries\":[{\"directory\":\"extra\",\"formats\":" + (scenario == "overlay-active" ? "48" : "99") + "}]}}" : "{\"pack\":{\"pack_format\":48}}";
        var entries = new Dictionary<string, string> { ["pack.mcmeta"] = metadata };
        if (scenario.StartsWith("tags"))
        {
            Directory.CreateDirectory(Path.Combine(f.World, "datapacks"));
            await File.WriteAllBytesAsync(Path.Combine(f.World, "datapacks/old.zip"), Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":48}}", ["data/minecraft/tags/function/load.json"] = "{\"values\":[\"test:old\"]}" }));
            entries["data/minecraft/tags/function/load.json"] = scenario == "tags-replace" ? "{\"replace\":true,\"values\":[]}" : "{\"values\":[\"test:new\"]}";
        }
        if (scenario.StartsWith("overlay"))
        {
            Directory.CreateDirectory(Path.Combine(f.World, "datapacks")); await File.WriteAllBytesAsync(Path.Combine(f.World, "datapacks/old.zip"), f.Pack("shared", "old"));
            entries["extra/data/test/function/shared.mcfunction"] = "new overlay";
        }
        f.Catalog.Add("main", Zip(entries));
        if (scenario is "minor-max" or "minor-min" or "tags-replace" or "overlay-active") await Reject(() => f.Service.PreviewWorldContentAsync(f.Request("main")));
        else { var preview = await f.Service.PreviewWorldContentAsync(f.Request("main")); if (scenario == "tags") Assert.IsTrue(preview.Warnings.Any(w => w.Contains("合并"))); await f.Service.InstallWorldContentAsync(preview.Id); }
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task WorldBundledDatapacksAreCheckedBeforeConfirmation(bool incompatible)
    {
        await using var f = await Fixture.Create(); var payload = incompatible ? Zip(new() { ["pack.mcmeta"] = "{\"pack\":{\"pack_format\":99}}" }) : f.Pack("world", "value");
        f.Catalog.Add("map", ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "map"), ["datapacks/payload.zip"] = payload }), kind: "world");
        if (incompatible) await Reject(() => f.Service.PreviewWorldContentAsync(f.Request("map", "world", "new")));
        else { var preview = await f.Service.PreviewWorldContentAsync(f.Request("map", "world", "new")); await f.Service.InstallWorldContentAsync(preview.Id); }
        Assert.IsFalse(Directory.EnumerateDirectories(f.Platform.Paths.TempDirectory, "world-inspect-*").Any());
    }
    [TestMethod]
    public async Task WorldGameplayResourcesNamedTokenAreNotMistakenForLauncherCredentials()
    {
        await using var f = await Fixture.Create();
        f.Catalog.Add("map", ZipBytes(new() { ["level.dat"] = Level(f.Version, f.DataVersion, "map"),
            ["datapacks/game/pack.mcmeta"] = Encoding.UTF8.GetBytes("{\"pack\":{\"pack_format\":48}}"),
            ["datapacks/game/data/test/function/token.mcfunction"] = Encoding.UTF8.GetBytes("say token") }), kind: "world");
        var preview = await f.Service.PreviewWorldContentAsync(f.Request("map", "world", "new"));
        await f.Service.InstallWorldContentAsync(preview.Id);
        Assert.AreEqual("say token", await File.ReadAllTextAsync(Path.Combine(f.Instance.GameDirectory, "saves/new/datapacks/game/data/test/function/token.mcfunction")));
    }
    private static async Task Reject(Func<Task> action)
    { try { await action(); Assert.Fail("Unsafe or interrupted installation unexpectedly succeeded"); } catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OperationCanceledException or AggregateException or JsonException) { } }
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().Select(p => Path.GetRelativePath(root, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public static byte[] Level(string version, int dataVersion, string name) => BoundedNbt.Write(new(new(NbtType.Compound, "", new NbtTag[] { new(NbtType.Compound, "Data", new NbtTag[] { new(NbtType.Int, "DataVersion", dataVersion), new(NbtType.Compound, "Version", new NbtTag[] { new(NbtType.String, "Name", version) }), new(NbtType.String, "LevelName", name) }) }), NbtCompression.Gzip));
    private static byte[] Zip(Dictionary<string, string> entries) => ZipBytes(entries.ToDictionary(p => p.Key, p => Encoding.UTF8.GetBytes(p.Value)));
    public static byte[] ZipBytes(Dictionary<string, byte[]> entries)
    { using var ms = new MemoryStream(); using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); } return ms.ToArray(); }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Bytes = new(); public Action? BeforeArchive;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); if (request.RequestUri!.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal)) BeforeArchive?.Invoke();
            var key = request.RequestUri.AbsolutePath == "/client.jar" ? "https://piston-data.mojang.com/client.jar" : request.RequestUri.AbsoluteUri;
            if (!Bytes.ContainsKey(key)) key = Bytes.Keys.Single(k => new Uri(k).AbsolutePath == request.RequestUri.AbsolutePath);
            var bytes = Bytes[key]; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class Catalog(string game, Handler handler) : IContentDependencyCatalogService, ILauncherCatalogService
    {
        private readonly Dictionary<string, ContentProject> projects = new(); private readonly Dictionary<string, ContentRelease> releases = new();
        public readonly Dictionary<string, ContentFile> Files = new(); public string? LastLoader;
        public void ModrinthDatapackRepresentation(string id) => projects[id] = projects[id] with { Kind = "mod" };
        public void Add(string id, byte[] bytes, string kind = "datapack", IReadOnlyList<ContentDependency>? dependencies = null)
        {
            var file = new ContentFile(id + ".zip", new("https://cdn.modrinth.com/data/" + id + ".zip"), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true); Files[id] = file; handler.Bytes[file.Uri.AbsoluteUri] = bytes;
            projects[id] = new(id, id, id, "", "", 1, kind, null, new("https://modrinth.com/datapack/" + id), ClientSide: "unsupported");
            releases[id] = new(id, id, "1", DateTimeOffset.UnixEpoch, [game], kind == "mod" ? ["fabric"] : kind == "world" ? [] : ["datapack"], [file], dependencies, id, "server_only");
        }
        public bool FailVersionLists;
        public void AddVersion(string project, string release, byte[] bytes, IReadOnlyList<ContentDependency>? dependencies = null, string? name = null)
        {
            var file = new ContentFile(name ?? release + ".zip", new("https://cdn.modrinth.com/data/" + release + "/" + (name ?? release + ".zip")), Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, true);
            Files[release] = file; handler.Bytes[file.Uri.AbsoluteUri] = bytes;
            releases[release] = new(release, release, release, DateTimeOffset.UnixEpoch.AddDays(releases.Count + 1), [game], ["datapack"], [file], dependencies, project, "server_only");
        }
        public Task<ContentProject> GetContentProjectAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(projects[id]);
        public Task<ContentRelease> GetContentReleaseAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(releases[id]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default)
        { LastLoader = loader; if (FailVersionLists) throw new HttpRequestException("controlled version listing failure"); return Task.FromResult<IReadOnlyList<ContentRelease>>(loader is null or "datapack" ? releases.Values.Where(r => r.ProjectId == projectId).ToArray() : []); }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Handler Handler = new(); public Catalog Catalog = null!; public PlatformServices Platform = null!; public LauncherService Service = null!; public GameInstance Instance = null!;
        private HttpClient http = null!; private IDisposable lease = null!;
        public string Version = "1.21.1"; public int DataVersion = 3955; public bool Modern;
        public string World => Path.Combine(Instance.GameDirectory, "saves/现有世界"); public string OtherWorld => Path.Combine(Instance.GameDirectory, "saves/另一世界"); public string Level => Path.Combine(World, "level.dat");
        public WorldContentRequest Request(string id, string kind = "datapack", string world = "现有世界") => new(Instance.Id, id, id, id + ".zip", kind, world);
        public byte[] Pack(string function, string content) => Zip(new() { ["pack.mcmeta"] = Modern ? "{\"pack\":{\"min_format\":[107,1],\"max_format\":107}}" : "{\"pack\":{\"pack_format\":48}}", ["data/test/function/" + function + ".mcfunction"] = content });
        public static async Task<Fixture> Create(bool modern = false)
        {
            var f = new Fixture { Modern = modern, Version = modern ? "26.2" : "1.21.1", DataVersion = modern ? 4903 : 3955 };
            f.Catalog = new(f.Version, f.Handler); f.http = new(f.Handler); var downloader = new VerifiedDownloader(f.http);
            f.Platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader); f.lease = f.Platform.DataLock.Acquire(f.Platform.Paths);
            var jar = Zip(new() { ["version.json"] = JsonSerializer.Serialize(new { id = f.Version, world_version = f.DataVersion, pack_version = modern ? (object)new { data_major = 107, data_minor = 1 } : new { data = 48 } }) });
            f.Handler.Bytes["https://piston-data.mojang.com/client.jar"] = jar;
            var cache = Path.Combine(f.Platform.Paths.CacheDirectory, "versions", f.Version); Directory.CreateDirectory(cache);
            await File.WriteAllTextAsync(Path.Combine(cache, f.Version + ".json"), JsonSerializer.Serialize(new { id = f.Version, mainClass = "fixture.ForbiddenGame", downloads = new { client = new { url = "https://piston-data.mojang.com/client.jar", sha1 = Convert.ToHexStringLower(SHA1.HashData(jar)), size = jar.Length } } }));
            f.Service = new(f.Platform, f.http, downloader, new(), worldContentCatalog: f.Catalog); await f.Service.InitializeAsync();
            f.Instance = await f.Service.CreateInstanceAsync("目标实例 中文", f.Version); Directory.CreateDirectory(f.World); Directory.CreateDirectory(f.OtherWorld);
            await File.WriteAllBytesAsync(f.Level, WorldContentInstallTests.Level(f.Version, f.DataVersion, "target")); await File.WriteAllBytesAsync(Path.Combine(f.OtherWorld, "level.dat"), WorldContentInstallTests.Level(f.Version, f.DataVersion, "other"));
            return f;
        }
        public async Task RestartServiceAsync()
        {
            await Service.DisposeAsync(); Service = new(Platform, http, new VerifiedDownloader(http), new(), worldContentCatalog: Catalog);
            await Service.InitializeAsync();
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); lease.Dispose(); await ((ProcessRunner)Platform.Processes).DisposeAsync(); http.Dispose(); }
    }
}
