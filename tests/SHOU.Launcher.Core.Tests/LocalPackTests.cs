using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LocalPackTests
{
    private const string FixtureKey = "synthetic-fixture-key-not-real";
    private sealed record Asset(int Project, int FileId, string Name, byte[] Bytes, int ClassId = 6, bool Fallback = false, bool Available = true, int FileStatus = 4)
    {
        public string Sha1 => Convert.ToHexStringLower(SHA1.HashData(Bytes));
        public string Sha512 => Convert.ToHexStringLower(SHA512.HashData(Bytes));
        public string Url => Fallback ? "https://cdn.modrinth.com/data/fixture/" + Name : "https://edge.forgecdn.net/files/1/2/" + Name;
    }
    private static Asset Mod(bool fallback = false) => new(101, 1001, "fixture.jar", Zip(("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"fixture\",\"version\":\"1.0.0\",\"environment\":\"client\"}")), Fallback: fallback);
    private static Asset Resource() => new(102, 1002, "fixture.zip", Zip(("pack.mcmeta", "{\"pack\":{\"pack_format\":34,\"description\":\"Synthetic resource pack\"}}")), 12);
    private static JsonObject Manifest(params Asset[] assets) => new()
    {
        ["manifestType"] = "minecraftModpack", ["manifestVersion"] = 1, ["name"] = "Synthetic CF pack", ["version"] = "1.0.0", ["overrides"] = "overrides",
        ["minecraft"] = new JsonObject { ["version"] = "1.21.1", ["modLoaders"] = new JsonArray(new JsonObject { ["id"] = "fabric-0.16.14", ["primary"] = true }) },
        ["files"] = new JsonArray(assets.Select(a => (JsonNode)new JsonObject { ["projectID"] = a.Project, ["fileID"] = a.FileId, ["required"] = true }).ToArray())
    };
    private static string Archive(JsonObject manifest, params (string Name, string Text)[] overrides)
    {
        var root = TestFiles.Root(); var path = Path.Combine(root, "source.zip"); File.WriteAllBytes(path, Zip(new[] { ("manifest.json", manifest.ToJsonString()) }.Concat(overrides).ToArray())); return path;
    }
    private sealed class FixtureApi(params Asset[] assets) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, string? Key)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!; var key = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null; Requests.Enqueue((uri.Host, key));
            if (uri.Host == "api.curseforge.com") Assert.AreEqual(FixtureKey, key); else Assert.IsNull(key, "API key reached a non-CurseForge request.");
            if (Override?.Invoke(request) is { } replaced) return Task.FromResult(replaced);
            if (uri.Host == "api.curseforge.com")
            {
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries); var project = int.Parse(segments[2]); var asset = assets.Single(a => a.Project == project);
                var data = segments.Length == 3 ? new JsonObject { ["id"] = project, ["gameId"] = 432, ["classId"] = asset.ClassId }
                    : new JsonObject { ["id"] = asset.FileId, ["modId"] = project, ["gameId"] = 432, ["isAvailable"] = asset.Available, ["fileStatus"] = asset.FileStatus, ["fileName"] = asset.Name, ["fileLength"] = asset.Bytes.Length,
                        ["downloadUrl"] = asset.Fallback ? null : asset.Url, ["hashes"] = new JsonArray(new JsonObject { ["algo"] = 1, ["value"] = asset.Sha1 }), ["gameVersions"] = new JsonArray("1.21.1", "Fabric"), ["dependencies"] = new JsonArray() };
                return Task.FromResult(ScriptedHttpHandler.Json(new JsonObject { ["data"] = data }.ToJsonString()));
            }
            if (uri.Host == "api.modrinth.com")
            {
                var asset = assets.Single(a => uri.AbsolutePath.EndsWith(a.Sha1, StringComparison.Ordinal)); Assert.AreEqual("?algorithm=sha1", uri.Query);
                return Task.FromResult(ScriptedHttpHandler.Json(new JsonObject { ["files"] = new JsonArray(new JsonObject { ["filename"] = asset.Name, ["url"] = asset.Url, ["size"] = asset.Bytes.Length, ["hashes"] = new JsonObject { ["sha1"] = asset.Sha1, ["sha512"] = asset.Sha512 } }) }.ToJsonString()));
            }
            if (uri.Host is "edge.forgecdn.net" or "cdn.modrinth.com")
            { var asset = assets.Single(a => uri.AbsolutePath.EndsWith("/" + a.Name, StringComparison.Ordinal)); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(asset.Bytes) }); }
            // Exercise the existing source resolver without allowing a fixture to contact any real host.
            if (uri.Scheme == "https" && uri.IsDefaultPort && (uri.Host is "meta.fabricmc.net" or "bmclapi2.bangbang93.com")
                && uri.AbsolutePath.EndsWith("/v2/versions/loader/1.21.1/0.16.14/profile/json", StringComparison.Ordinal))
                return Task.FromResult(ScriptedHttpHandler.Json("{\"id\":\"fabric-loader-0.16.14-1.21.1\",\"inheritsFrom\":\"1.21.1\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.16.14\"}]}"));
            throw new AssertFailedException("Unexpected fixture host: " + uri.Host);
        }
    }
    [TestMethod]
    public async Task FormatDetectionUsesManifestContentAndNeverTreatsArbitraryZipAsShou()
    {
        var reader = new LocalPackArchive(); var cf = await reader.ReadAsync(Archive(Manifest(Mod()))); Assert.AreEqual(LocalPackFormat.CurseForge, cf.Inspection.Format); Assert.IsFalse(cf.Inspection.CanImport);
        var root = TestFiles.Root(); var unknown = Path.Combine(root, "unknown.mrpack"); await File.WriteAllBytesAsync(unknown, Zip(("readme.txt", "not a pack"))); Assert.AreEqual(LocalPackFormat.Unknown, (await reader.ReadAsync(unknown)).Inspection.Format);
        var mmc = Path.Combine(root, "multimc.zip"); await File.WriteAllBytesAsync(mmc, Zip(("Instance/mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}]}"), ("Instance/instance.cfg", "name=Fixture\nPreLaunchCommand=must-never-run")));
        var recognized = await reader.ReadAsync(mmc); Assert.AreEqual(LocalPackFormat.MultiMC, recognized.Inspection.Format); Assert.IsFalse(recognized.Inspection.CanImport);
        var shou = Path.Combine(root, "shou.zip"); await File.WriteAllBytesAsync(shou, Zip(("shou-instance.json", "{\"schemaVersion\":1,\"Name\":\"Fixture export\"}")));
        Assert.AreEqual(LocalPackFormat.ShouExport, (await reader.ReadAsync(shou)).Inspection.Format);
    }
    [TestMethod]
    public async Task ResolverPlacesOfficialResourceZipCorrectlyAndExactHashFallbackNeverReceivesKey()
    {
        var mod = Mod(true); var resource = Resource(); using var handler = new FixtureApi(mod, resource); using var http = new HttpClient(handler); var resolver = new CurseForgePackResolver(http, FixtureKey);
        var result = await resolver.ResolveAsync([new(mod.Project, mod.FileId, true), new(resource.Project, resource.FileId, true)], "1.21.1", "fabric");
        Assert.IsEmpty(result.BlockedFiles); Assert.HasCount(2, result.Files); Assert.AreEqual("mods/fixture.jar", result.Files.Single(f => f.ProjectId == mod.Project).Path); Assert.AreEqual("resourcepacks/fixture.zip", result.Files.Single(f => f.ProjectId == resource.Project).Path);
        Assert.IsTrue(result.Files.Single(f => f.ProjectId == mod.Project).FromModrinth); Assert.AreEqual(mod.Sha512, result.Files.Single(f => f.ProjectId == mod.Project).Sha512);
        var target = TestFiles.Root(); foreach (var file in result.Files) await resolver.DownloadAsync(file, Path.Combine(target, Path.GetFileName(file.Path)));
        Assert.IsTrue(handler.Requests.Any(r => r.Host == "api.modrinth.com" && r.Key is null)); Assert.IsTrue(handler.Requests.Any(r => r.Host == "cdn.modrinth.com" && r.Key is null)); Assert.IsTrue(handler.Requests.Any(r => r.Host == "edge.forgecdn.net" && r.Key is null));
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("同 SHA-1", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task ArchivedRequiredPackDependencyStagesOnlyTheOfficialUrlWithHashAndWarning()
    {
        var resource = Resource() with { Available = false, FileStatus = 8 }; using var handler = new FixtureApi(resource); using var http = new HttpClient(handler); var resolver = new CurseForgePackResolver(http, FixtureKey);
        var source = Archive(Manifest(resource)); var archive = new LocalPackArchive(); var read = await archive.ReadAsync(source);
        var result = await resolver.ResolveAsync(read.CurseForgeFiles, read.Inspection.GameVersion, read.Inspection.Loader);
        Assert.IsEmpty(result.BlockedFiles); Assert.HasCount(1, result.Files); Assert.AreEqual(resource.Url, result.Files[0].DownloadUri.AbsoluteUri); Assert.IsFalse(result.Files[0].FromModrinth);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("已归档的历史包依赖", StringComparison.Ordinal)));
        var stage = Path.Combine(TestFiles.Root(), "stage"); await archive.StageCurseForgeAsync(source, read.Inspection.ArchiveSha256, stage, result.Files, (file, target, ct) => resolver.DownloadAsync(file, target, ct: ct));
        CollectionAssert.AreEqual(resource.Bytes, await File.ReadAllBytesAsync(Path.Combine(stage, "resourcepacks/fixture.zip"))); Assert.IsFalse(handler.Requests.Any(r => r.Host == "api.modrinth.com"));
        Assert.IsTrue(handler.Requests.Any(r => r.Host == "edge.forgecdn.net" && r.Key is null));
        var count = handler.Requests.Count; var optional = await resolver.ResolveAsync([new(resource.Project, resource.FileId, false)], "1.21.1", "fabric"); Assert.IsEmpty(optional.Files); Assert.AreEqual(count, handler.Requests.Count, "The archived exception must not resolve unselected optional files.");
    }
    [TestMethod]
    [DataRow(8, false, true, false, "未提供官方 CDN")]
    [DataRow(4, false, false, false, "不可供下载")]
    [DataRow(3, false, false, false, "审核中")]
    [DataRow(0, false, false, false, "状态不明")]
    [DataRow(5, true, false, false, "审核已拒绝")]
    [DataRow(6, true, false, false, "恶意软件")]
    [DataRow(7, true, false, false, "已删除")]
    [DataRow(3, true, false, true, null)]
    [DataRow(0, true, false, true, null)]
    [DataRow(12, true, false, true, null)]
    public async Task FileStatusDoesNotGeneralizeTheArchivedDependencyException(int status, bool available, bool noUrl, bool canResolve, string? reason)
    {
        var asset = Mod() with { FileStatus = status, Available = available, Fallback = noUrl }; using var handler = new FixtureApi(asset); using var http = new HttpClient(handler);
        var result = await new CurseForgePackResolver(http, FixtureKey).ResolveAsync([new(asset.Project, asset.FileId, true)], "1.21.1", "fabric");
        Assert.AreEqual(canResolve ? 1 : 0, result.Files.Count); Assert.AreEqual(canResolve ? 0 : 1, result.BlockedFiles.Count);
        if (reason is not null) Assert.IsTrue(result.BlockedFiles[0].Contains(reason, StringComparison.Ordinal));
        Assert.IsTrue(handler.Requests.All(r => r.Host == "api.curseforge.com"), "Unavailable or archived files without a direct URL must not trigger hash fallback or CDN downloads.");
    }
    [TestMethod]
    public async Task MissingKeyAndApiDenialListEveryUnresolvedRequiredFileWithoutLeakingSecret()
    {
        using var emptyHttp = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Missing key must not access a network.")));
        var references = new CurseForgePackReference[] { new(1, 10, true), new(2, 20, true), new(3, 30, false) };
        Assert.HasCount(2, (await new CurseForgePackResolver(emptyHttp, null).ResolveAsync(references, "1.21.1", "fabric")).BlockedFiles);
        using var denied = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(FixtureKey) })));
        var result = await new CurseForgePackResolver(denied, FixtureKey).ResolveAsync(references, "1.21.1", "fabric"); Assert.HasCount(2, result.BlockedFiles); Assert.IsEmpty(result.Files);
        Assert.IsFalse(string.Join('\n', result.BlockedFiles).Contains(FixtureKey, StringComparison.Ordinal));
    }
    [TestMethod, DataRow("redirect"), DataRow("hash"), DataRow("size"), DataRow("host"), DataRow("not-found")]
    public async Task UnverifiedMetadataAndHashFallbackCannotInventSuccessfulFiles(string scenario)
    {
        var mod = Mod(true); using var handler = new FixtureApi(mod); using var http = new HttpClient(handler);
        handler.Override = request =>
        {
            if (scenario == "redirect") return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new("https://unknown.invalid/") } };
            if (request.RequestUri!.Host != "api.modrinth.com") return null;
            if (scenario == "not-found") return new(HttpStatusCode.NotFound);
            var sha1 = scenario == "hash" ? new string('0', 40) : mod.Sha1; var size = scenario == "size" ? mod.Bytes.Length + 1 : mod.Bytes.Length; var url = scenario == "host" ? "https://unknown.invalid/fixture.jar" : mod.Url;
            return ScriptedHttpHandler.Json(new JsonObject { ["files"] = new JsonArray(new JsonObject { ["url"] = url, ["size"] = size, ["hashes"] = new JsonObject { ["sha1"] = sha1, ["sha512"] = mod.Sha512 } }) }.ToJsonString());
        };
        var result = await new CurseForgePackResolver(http, FixtureKey).ResolveAsync([new(mod.Project, mod.FileId, true)], "1.21.1", "fabric"); Assert.HasCount(1, result.BlockedFiles); Assert.IsEmpty(result.Files); Assert.IsFalse(handler.Requests.Any(r => r.Host == "unknown.invalid"));
    }
    [TestMethod]
    public async Task StagingUsesActualHashesAndCrcAndKeepsResourceAndConfigPayloads()
    {
        var mod = Mod(true); var resource = Resource(); using var handler = new FixtureApi(mod, resource); using var http = new HttpClient(handler); var resolver = new CurseForgePackResolver(http, FixtureKey);
        var source = Archive(Manifest(mod, resource), ("overrides/config/fixture.json", "{\"fixture\":true}"), ("overrides/launcher_accounts.json", "must not import"), ("overrides/config/execute.sh", "must not execute"));
        var archive = new LocalPackArchive(); var inspection = await archive.ReadAsync(source); var resolution = await resolver.ResolveAsync(inspection.CurseForgeFiles, "1.21.1", "fabric"); var stage = Path.Combine(TestFiles.Root(), "stage");
        await archive.StageCurseForgeAsync(source, inspection.Inspection.ArchiveSha256, stage, resolution.Files, (file, path, ct) => resolver.DownloadAsync(file, path, ct: ct));
        CollectionAssert.AreEqual(mod.Bytes, await File.ReadAllBytesAsync(Path.Combine(stage, "mods/fixture.jar"))); CollectionAssert.AreEqual(resource.Bytes, await File.ReadAllBytesAsync(Path.Combine(stage, "resourcepacks/fixture.zip")));
        Assert.AreEqual("{\"fixture\":true}", await File.ReadAllTextAsync(Path.Combine(stage, "config/fixture.json"))); Assert.IsFalse(File.Exists(Path.Combine(stage, "launcher_accounts.json"))); Assert.IsFalse(File.Exists(Path.Combine(stage, "config/execute.sh")));
        var manifest = await ContentInstallFiles.ReadManifestAsync(new("unregistered", "Fixture", "1.21.1", stage)); Assert.HasCount(3, manifest.Files); Assert.IsNull(manifest.Files.Single(f => f.Kind == "resourcepack").DownloadUrl);
        Assert.IsFalse(Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(".download.lock", StringComparison.Ordinal)));
    }
    [TestMethod, DataRow("manifest.json"), DataRow("overrides/config/example.json")]
    public async Task CorruptLocalZipEntryCrcIsRejectedAndNoInstanceIsPublished(string entryName)
    {
        var path = Archive(Manifest(), ("overrides/config/example.json", "{}")); var data = await File.ReadAllBytesAsync(path); CorruptCrc(data, entryName); await File.WriteAllBytesAsync(path, data);
        var reader = new LocalPackArchive();
        if (entryName == "manifest.json") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.ReadAsync(path));
        else
        {
            var inspection = await reader.ReadAsync(path); var stage = Path.Combine(TestFiles.Root(), "stage");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.StageCurseForgeAsync(path, inspection.Inspection.ArchiveSha256, stage, [], (_, _, _) => throw new AssertFailedException("No required files exist.")));
        }
    }
    [TestMethod]
    public async Task TraversalLinksAndFileDirectoryCollisionsAreRejectedBeforeMetadataRequests()
    {
        foreach (var name in new[] { "overrides/../outside", "overrides/config/file", "overrides/config/link" })
        {
            var entries = name.EndsWith("file", StringComparison.Ordinal) ? new[] { (name, "first"), (name + "/nested", "second") } : new[] { (name, "fixture") };
            var path = Archive(Manifest(), entries); if (name.EndsWith("link", StringComparison.Ordinal)) { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); zip.GetEntry(name)!.ExternalAttributes = unchecked((int)0xa1ff0000); }
            await Assert.ThrowsAsync<InvalidDataException>(() => new LocalPackArchive().ReadAsync(path));
        }
    }
    [TestMethod, DataRow("success"), DataRow("cancel"), DataRow("persist-failure")]
    public async Task ServicePublishesOnlyVerifiedNewInstancesAndRollsBackCancellationOrFailedPersistence(string scenario)
    {
        var mod = Mod(true); var resource = Resource(); using var handler = new FixtureApi(mod, resource); using var http = new HttpClient(handler); var resolver = new CurseForgePackResolver(http, FixtureKey);
        var source = Archive(Manifest(mod, resource), ("overrides/config/client.json", "{}")); var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader) with { Credentials = new EmptyCredentials() };
        using var lease = platform.DataLock.Acquire(platform.Paths); await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions(), localPackResolver: resolver); await service.InitializeAsync(); var inspection = await service.InspectLocalPackAsync(source); Assert.IsTrue(inspection.CanImport);
        if (scenario == "persist-failure") Directory.CreateDirectory(Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json")); using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") handler.Override = request => { if (request.RequestUri!.Host == "cdn.modrinth.com") cancellation.Cancel(); return null; };
        var request = new LocalPackImportRequest(source, inspection.ArchiveSha256, "Imported fixture");
        if (scenario == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => service.ImportLocalPackAsync(request, cancellation.Token));
        else if (scenario == "persist-failure") await Assert.ThrowsAsync<IOException>(() => service.ImportLocalPackAsync(request));
        else
        {
            var result = await service.ImportLocalPackAsync(request); Assert.HasCount(1, service.Snapshot.Instances); Assert.AreEqual("fabric", result.Instance.Loader); Assert.AreEqual("0.16.14", result.Instance.LoaderVersion); Assert.AreEqual("1.21.1", result.Instance.GameVersionId); Assert.IsFalse(result.Instance.Installed);
            Assert.IsTrue(File.Exists(Path.Combine(result.Instance.GameDirectory, "resourcepacks/fixture.zip"))); Assert.AreEqual(result.Instance.Id, (await ContentInstallFiles.ReadManifestAsync(result.Instance)).InstanceId);
            await using var store = new ConfigurationStore(platform.Paths); Assert.HasCount(1, (await store.LoadAsync()).Instances); return;
        }
        Assert.IsEmpty(service.Snapshot.Instances); Assert.IsEmpty(Directory.GetDirectories(platform.Paths.InstancesDirectory));
    }
    [TestMethod]
    public async Task DedicatedPackTransportDoesNotFollowRealLoopbackRedirect()
    {
        var secondHits = 0; using var second = new LocalHttpFixture(context => { Interlocked.Increment(ref secondHits); context.Response.StatusCode = 200; context.Response.Close(); });
        using var first = new LocalHttpFixture(context => { context.Response.StatusCode = 302; context.Response.RedirectLocation = second.Uri("target").AbsoluteUri; context.Response.Close(); });
        var network = new NetworkOptions(); network.Update(new(Proxy: new(ProxyMode.Direct))); using var http = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        using var response = await http.GetAsync(first.Uri("redirect")); Assert.AreEqual(HttpStatusCode.Found, response.StatusCode); Assert.AreEqual(0, secondHits);
    }
    private sealed class EmptyCredentials : ICredentialStore
    {
        public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
    private static byte[] Zip(params (string Name, string Text)[] entries)
    { using var result = new MemoryStream(); using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true)) foreach (var (name, value) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(value); } return result.ToArray(); }
    private static void CorruptCrc(byte[] bytes, string entryName)
    {
        for (var offset = 0; offset < bytes.Length - 46; offset++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x02014b50)
        { var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28)); if (Encoding.UTF8.GetString(bytes, offset + 46, size) == entryName) { bytes[offset + 16] ^= 1; return; } }
        throw new AssertFailedException("Fixture central entry not found.");
    }
}
