using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ContentInstallTests
{
    [TestMethod]
    public async Task RequiredDependenciesInstallActualFilesAndChecksummedManifestWithoutOptionalDownloads()
    {
        var fixture = new Fixture(); fixture.Catalog.Add("api", []); fixture.Catalog.Add("main", [new("required", "api"), new("optional", "extra")]);
        fixture.Catalog.Add("api", [], "client-api");
        fixture.Catalog.Releases["api"] = fixture.Catalog.Releases["api"] with { Environment = "dedicated_server_only", Published = DateTimeOffset.UtcNow.AddDays(1) };
        var plan = await fixture.Plan(); Assert.HasCount(2, plan.Files); Assert.IsTrue(plan.Files[0].Dependency); Assert.HasCount(1, plan.Warnings);
        Assert.AreEqual("client-api", plan.Files[0].ReleaseId);
        var result = await ContentInstallFiles.InstallAsync(fixture.Instance, plan, fixture.Download);
        Assert.AreEqual(2, result.AddedFiles); CollectionAssert.AreEqual(fixture.Catalog.Bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "mods/main.jar")));
        var manifest = await ContentInstallFiles.ReadManifestAsync(fixture.Instance);
        Assert.HasCount(2, manifest.Files); Assert.IsTrue(manifest.Files.All(f => f.Sha512.Length == 128));
        var repeat = await ContentInstallFiles.InstallAsync(fixture.Instance, plan, fixture.Download);
        Assert.AreEqual(0, repeat.AddedFiles); Assert.HasCount(2, (await ContentInstallFiles.ReadManifestAsync(fixture.Instance)).Files);
        Assert.IsFalse(Directory.EnumerateDirectories(fixture.Root, ".shou-content-stage-*").Any());
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("version")]
    [DataRow("game")]
    [DataRow("incompatible")]
    [DataRow("server")]
    [DataRow("unicode")]
    public async Task InvalidDependencyGraphsNeverCreateInstalledFiles(string scenario)
    {
        var f = new Fixture(); f.Catalog.Add("api", []); f.Catalog.Add("main", [new("required", "api")]);
        if (scenario == "cycle") f.Catalog.Add("api", [new("required", "main")]);
        if (scenario == "version") { f.Catalog.Add("api", [], "old"); f.Catalog.Add("main", [new("required", "api", "api"), new("required", "api", "old")]); }
        if (scenario == "game") f.Catalog.Releases["api"] = f.Catalog.Releases["api"] with { GameVersions = ["other-version"] };
        if (scenario == "incompatible") f.Catalog.Add("main", [new("required", "api"), new("incompatible", "api")]);
        if (scenario == "server") f.Catalog.Releases["api"] = f.Catalog.Releases["api"] with { Environment = "dedicated_server_only" };
        if (scenario == "unicode")
        {
            f.Catalog.Add("other", []); f.Catalog.Add("main", [new("required", "api"), new("required", "other")]);
            f.Catalog.Releases["api"] = f.Catalog.Releases["api"] with { Files = [f.Catalog.Releases["api"].Files[0] with { FileName = "é.jar" }] };
            f.Catalog.Releases["other"] = f.Catalog.Releases["other"] with { Files = [f.Catalog.Releases["other"].Files[0] with { FileName = "e\u0301.jar" }] };
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Plan());
        Assert.AreEqual(0, Directory.GetFileSystemEntries(f.Root).Length);
    }

    [TestMethod]
    public async Task ExistingDifferentFileFailsPreflightWithoutDownloadingOrOverwriting()
    {
        var f = new Fixture(); f.Catalog.Add("main", []); Directory.CreateDirectory(Path.Combine(f.Root, "mods"));
        var target = Path.Combine(f.Root, "mods/main.jar"); await File.WriteAllTextAsync(target, "existing private bytes");
        await Assert.ThrowsAsync<IOException>(() => f.Plan());
        Assert.AreEqual("existing private bytes", await File.ReadAllTextAsync(target));
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, ContentInstallFiles.ManifestName)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadFailureOrCancellationLeavesInstanceAndManifestUntouched(bool cancel)
    {
        var f = new Fixture(); f.Catalog.Add("api", []); f.Catalog.Add("main", [new("required", "api")]); var plan = await f.Plan();
        using var cancellation = new CancellationTokenSource(); var count = 0;
        async Task Download(ContentFile file, string target, CancellationToken ct)
        {
            await f.Download(file, target, ct);
            if (++count == 2) { if (cancel) cancellation.Cancel(); else throw new IOException("controlled fixture failure"); }
        }
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => ContentInstallFiles.InstallAsync(f.Instance, plan, Download, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => ContentInstallFiles.InstallAsync(f.Instance, plan, Download));
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, "mods/main.jar"))); Assert.IsFalse(File.Exists(Path.Combine(f.Root, "mods/api.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, ContentInstallFiles.ManifestName)));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Root, ".shou-content-stage-*").Any());
    }

    [TestMethod]
    public async Task FailedManifestCommitRollsBackOnlyNewFilesAndRetainsPreexistingIdenticalDependency()
    {
        var f = new Fixture(); f.Catalog.Add("api", []); f.Catalog.Add("main", [new("required", "api")]);
        Directory.CreateDirectory(Path.Combine(f.Root, "mods")); await File.WriteAllBytesAsync(Path.Combine(f.Root, "mods/api.jar"), f.Catalog.Bytes);
        var plan = await f.Plan();
        async Task Download(ContentFile file, string target, CancellationToken ct)
        { await f.Download(file, target, ct); Directory.CreateDirectory(Path.Combine(f.Root, ContentInstallFiles.ManifestName)); }
        await Assert.ThrowsAsync<IOException>(() => ContentInstallFiles.InstallAsync(f.Instance, plan, Download));
        Assert.IsFalse(File.Exists(Path.Combine(f.Root, "mods/main.jar")));
        CollectionAssert.AreEqual(f.Catalog.Bytes, await File.ReadAllBytesAsync(Path.Combine(f.Root, "mods/api.jar")));
    }

    [TestMethod]
    public async Task InternalFolderLinksAndTraversalNeverBecomeInstallTargets()
    {
        var f = new Fixture(); f.Catalog.Add("main", []); var alias = Path.Combine(f.Root, "alias"); Directory.CreateDirectory(alias);
        Directory.CreateSymbolicLink(Path.Combine(f.Root, "mods"), alias);
        await Assert.ThrowsAsync<IOException>(() => f.Plan()); Assert.IsEmpty(Directory.GetFiles(alias));
        Assert.ThrowsExactly<InvalidDataException>(() => ContentInstallFiles.Literal(f.Root, "mods/../escape.jar"));
    }

    [TestMethod]
    public async Task ExcessiveRequiredDepthStopsBeforeAnyFileMutation()
    {
        var f = new Fixture();
        for (var i = 20; i >= 0; i--) f.Catalog.Add(i == 0 ? "main" : "dep" + i, i == 20 ? [] : [new("required", "dep" + (i + 1))]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Plan()); Assert.IsEmpty(Directory.GetFileSystemEntries(f.Root));
    }

    [TestMethod]
    public async Task UnicodeNamedResourceKeepsIdentityOnRepeatedInstall()
    {
        var f = new Fixture(); f.Catalog.Add("main", []);
        f.Catalog.Releases["main"] = f.Catalog.Releases["main"] with { Files = [f.Catalog.Releases["main"].Files[0] with { FileName = "é.jar" }] };
        var request = new ContentInstallRequest("instance", "main", "main", "é.jar", "mod");
        var planner = new ContentInstallPlanner(f.Catalog, f.Catalog);
        var plan = await planner.PlanAsync(f.Instance, "1.21.1", request);
        await ContentInstallFiles.InstallAsync(f.Instance, plan, f.Download);
        var repeat = await planner.PlanAsync(f.Instance, "1.21.1", request);
        Assert.AreEqual(0, (await ContentInstallFiles.InstallAsync(f.Instance, repeat, f.Download)).AddedFiles);
        Assert.HasCount(1, (await ContentInstallFiles.ReadManifestAsync(f.Instance)).Files);
    }

    private sealed class Fixture
    {
        public string Root { get; } = TestFiles.Root();
        public FixtureCatalog Catalog { get; } = new();
        public GameInstance Instance => new("instance", "Fixture instance", "fabric-loader-0.16.5-1.21.1", Root, Loader: "fabric", LoaderVersion: "0.16.5");
        public Task<ContentInstallPlan> Plan() => new ContentInstallPlanner(Catalog, Catalog).PlanAsync(Instance, "1.21.1", new("instance", "main", "main", "main.jar", "mod"));
        public Task Download(ContentFile file, string target, CancellationToken ct) => File.WriteAllBytesAsync(target, Catalog.Bytes, ct);
    }
    private sealed class FixtureCatalog : ILauncherCatalogService, IContentDependencyCatalogService
    {
        public byte[] Bytes { get; } = [11, 22, 33, 44];
        public Dictionary<string, ContentRelease> Releases { get; } = new(StringComparer.Ordinal);
        public void Add(string id, IReadOnlyList<ContentDependency> dependencies, string? versionId = null)
        {
            var file = new ContentFile(id + ".jar", new("https://cdn.modrinth.com/data/" + id + ".jar"), Convert.ToHexStringLower(SHA1.HashData(Bytes)), Bytes.Length, true);
            Releases[versionId ?? id] = new(versionId ?? id, id, "1.0", DateTimeOffset.UtcNow, ["1.21.1"], ["fabric"], [file], dependencies, id, "client_and_server");
        }
        public Task<ContentRelease> GetContentReleaseAsync(string releaseId, CancellationToken cancellationToken = default) => Task.FromResult(Releases[releaseId]);
        public Task<ContentProject> GetContentProjectAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new ContentProject(projectId, projectId, projectId, "", "", 0, "mod", null, new("https://modrinth.com/mod/" + projectId), "required"));
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentRelease>>(Releases.Values.Where(r => r.ProjectId == projectId).ToArray());
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
