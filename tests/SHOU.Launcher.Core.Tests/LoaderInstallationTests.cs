using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Loaders;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LoaderInstallationTests
{
    [TestMethod]
    public async Task OfficialCatalogFiltersGameVersionsAndOrdersNumericLoaderVersions()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<metadata><versioning><versions><version>1.21.1-52.1.9</version><version>1.21.1-52.1.21</version><version>1.21-51.0.0</version></versions></versioning></metadata>") })));
        var choices = await new LoaderCatalog(http).GetAsync("forge", "1.21.1", default);
        CollectionAssert.AreEqual(new[] { "52.1.21", "52.1.9" }, choices.Select(v => v.Version).ToArray());
        Assert.AreEqual("21.1.", LoaderCatalog.NeoPrefix("1.21.1"));
        Assert.AreEqual("21.0.", LoaderCatalog.NeoPrefix("1.21"));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LoaderCatalog.NeoPrefix("24w01a"));
    }

    [TestMethod]
    public async Task QuiltCatalogWithoutStablePropertyPrefersCurrentFormalVersion()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("""[{"loader":{"version":"0.20.0-beta.9"}},{"loader":{"version":"0.30.1"}},{"loader":{"version":"0.30.2"}}]"""))));
        var choices = await new LoaderCatalog(http).GetAsync("quilt", "1.21.1", default);
        Assert.AreEqual("0.30.2", choices[0].Version); Assert.IsTrue(choices[0].Stable); Assert.IsFalse(choices[^1].Stable);
    }

    [TestMethod]
    public async Task CompletedProcessorImportsGeneratedLibraryAndReceiptSkipsRepeatExecution()
    {
        using var fixture = new Fixture(); var recipe = await fixture.Cache();
        await fixture.Install(recipe); Assert.AreEqual(1, fixture.Runner.Count);
        var library = Path.Combine(fixture.CacheRoot, "libraries/fixture/generated.jar");
        CollectionAssert.AreEqual(Fixture.Generated, await File.ReadAllBytesAsync(library));
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Profile(recipe)))!;
        Assert.AreEqual(true, (bool?)profile["libraries"]?[0]?["downloads"]?["artifact"]?["shouGenerated"]);
        await fixture.Install(recipe); Assert.AreEqual(1, fixture.Runner.Count);
        Assert.IsEmpty(Directory.GetDirectories(fixture.TempRoot));
        await File.WriteAllTextAsync(library, "tampered");
        await Assert.ThrowsAsync<IOException>(() => fixture.Install(recipe));
        Assert.AreEqual("tampered", await File.ReadAllTextAsync(library));
        Assert.IsEmpty(Directory.GetDirectories(fixture.TempRoot));
    }

    [TestMethod]
    [DataRow("failure")]
    [DataRow("cancel")]
    [DataRow("wrong-parent")]
    [DataRow("wrong-main")]
    [DataRow("bloat")]
    [DataRow("link")]
    public async Task FailedProcessorCannotCommitProfileReceiptOrEscapeStage(string failure)
    {
        using var fixture = new Fixture(failure); var recipe = await fixture.Cache();
        var before = await File.ReadAllTextAsync(fixture.Profile(recipe));
        try { await fixture.Install(recipe); Assert.Fail("Fixture processor should fail."); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Profile(recipe)));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Profile(recipe))!, "loader-installed.json")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CacheRoot, "libraries/fixture/generated.jar")));
        Assert.IsEmpty(Directory.GetDirectories(fixture.TempRoot));
    }

    [TestMethod]
    public async Task IdentityMismatchAndOversizedInstallerStopBeforeProcessExecution()
    {
        using var bad = new Fixture("wrong-id");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => bad.Cache()); Assert.AreEqual(0, bad.Runner.Count);
        using var large = new Fixture("oversize");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => large.Cache()); Assert.AreEqual(0, large.Downloads.Count);
    }

    [TestMethod]
    public async Task RecipeAndProfileAliasesCannotOverwriteAnotherCacheFile()
    {
        foreach (var name in new[] { "loader-recipe.json", "1.21.1-forge-52.1.21.json" })
        {
            using var f = new Fixture(); var folder = Path.Combine(f.CacheRoot, "versions/1.21.1-forge-52.1.21"); Directory.CreateDirectory(folder);
            var sentinel = Path.Combine(f.CacheRoot, "keep.json"); await File.WriteAllTextAsync(sentinel, "preserve this cache file");
            File.CreateSymbolicLink(Path.Combine(folder, name), sentinel);
            await Assert.ThrowsAsync<IOException>(() => f.Cache()); Assert.AreEqual("preserve this cache file", await File.ReadAllTextAsync(sentinel));
        }
    }

    [TestMethod]
    public async Task CredentialProxyIsRejectedAndJavaOptionsAreContained()
    {
        using var fixture = new Fixture(); var recipe = await fixture.Cache();
        fixture.Proxy = new(ProxyMode.Custom, "http://127.0.0.1:7890", "private-key");
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => fixture.Install(recipe)); Assert.AreEqual(0, fixture.Runner.Count);
        fixture.Proxy = new(ProxyMode.Custom, "http://127.0.0.1:7890");
        await fixture.Install(recipe); Assert.AreEqual(1, fixture.Runner.Count);
        var spec = fixture.Runner.Last!;
        Assert.IsNull(spec.SensitiveInput);
        Assert.IsNull(spec.EnvironmentOverrides!["JAVA_TOOL_OPTIONS"]);
        Assert.IsFalse(spec.EnvironmentOverrides.ContainsKey("HOME"));
        Assert.IsTrue(spec.Arguments.Contains("-Dhttps.proxyPort=7890"));
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Generated = Encoding.UTF8.GetBytes("controlled processor output");
        private static readonly byte[] Client = Encoding.UTF8.GetBytes("controlled vanilla client");
        private readonly HttpClient http;
        internal readonly FixtureDownloads Downloads;
        internal readonly FixtureRunner Runner;
        internal string CacheRoot { get; } = Path.Combine(TestFiles.Root(), "cache");
        internal string TempRoot { get; } = Path.Combine(TestFiles.Root(), "temp");
        internal ProxySettings Proxy { get; set; } = new(ProxyMode.Direct);
        private OfficialLoaderInstaller Installer => new(http, Downloads, Runner, CacheRoot, TempRoot, () => DownloadSources.Defaults(), () => Proxy);
        internal Fixture(string failure = "")
        {
            Directory.CreateDirectory(TempRoot); Directory.CreateDirectory(CacheRoot);
            var profile = ProfileNode(failure == "wrong-id" ? "wrong-id" : "1.21.1-forge-52.1.21", "1.21.1");
            using var jar = new MemoryStream();
            using (var zip = new ZipArchive(jar, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("install_profile.json").Open())) writer.Write("{\"minecraft\":\"1.21.1\",\"libraries\":[]}");
                using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open())) writer.Write(profile.ToJsonString());
            }
            var bytes = jar.ToArray(); Downloads = new(bytes, Client); Runner = new(failure);
            http = new(new ScriptedHttpHandler(request =>
            {
                var content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(Encoding.UTF8.GetBytes(Hash(bytes)));
                if (request.Method == HttpMethod.Head) content.Headers.ContentLength = failure == "oversize" ? 40 * 1024 * 1024 : bytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }));
        }
        internal Task<LoaderRecipe> Cache() => Installer.CacheAsync("forge", "1.21.1", "52.1.21", default);
        internal string Profile(LoaderRecipe recipe) => Path.Combine(CacheRoot, "versions", recipe.ProfileId, recipe.ProfileId + ".json");
        internal Task Install(LoaderRecipe recipe)
        {
            var vanilla = new JsonObject { ["id"] = "1.21.1", ["mainClass"] = "fixture.Main", ["libraries"] = new JsonArray(),
                ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["url"] = "https://example.invalid/client", ["sha1"] = Hash(Client), ["size"] = (long)Client.Length } } };
            var instance = new GameInstance("fixture", "Fixture", recipe.ProfileId, TestFiles.Root(), Loader: "forge", LoaderVersion: "52.1.21", GameVersionId: "1.21.1");
            return Installer.EnsureInstalledAsync(instance, new("1.21.1", vanilla, "fixture", []), new("/fixture/java", "/fixture", "21", 21, "fixture", RuntimeArchitecture.Arm64, "fixture"), new("osx", "26", RuntimeArchitecture.Arm64), null, default);
        }
        internal static JsonObject ProfileNode(string id, string parent) => new()
        {
            ["id"] = id, ["inheritsFrom"] = parent, ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            ["libraries"] = new JsonArray(new JsonObject { ["name"] = "fixture:generated:1", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = "fixture/generated.jar", ["url"] = "" } } })
        };
        internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
        public void Dispose() => http.Dispose();
    }
    private sealed class FixtureDownloads(byte[] jar, byte[] client) : IVerifiedDownloader
    {
        internal int Count;
        public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Count++; var bytes = request.DestinationPath.EndsWith("-installer.jar", StringComparison.Ordinal) ? jar : client;
            Assert.AreEqual(Fixture.Hash(bytes), request.Sha1); Assert.AreEqual((long)bytes.Length, request.Size);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!); await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
        }
    }
    private sealed class FixtureRunner(string failure) : IProcessRunner
    {
        internal int Count; internal ProcessSpec? Last;
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("Only bounded installer RunAsync is allowed.");
        public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default)
        {
            Count++; Last = spec;
            Assert.IsTrue(File.Exists(Path.Combine(spec.WorkingDirectory, "launcher_profiles.json")));
            Assert.IsTrue(File.Exists(Path.Combine(spec.WorkingDirectory, "versions/1.21.1/1.21.1.jar")));
            if (failure == "failure") return new(1, "controlled failure", "");
            if (failure == "cancel") return new(-1, "", "", Cancelled: true);
            var path = Path.Combine(spec.WorkingDirectory, "versions/1.21.1-forge-52.1.21/1.21.1-forge-52.1.21.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var profile = Fixture.ProfileNode("1.21.1-forge-52.1.21", failure == "wrong-parent" ? "1.20" : "1.21.1");
            if (failure == "wrong-main") profile["mainClass"] = "wrong.Main";
            await File.WriteAllTextAsync(path, profile.ToJsonString(), cancellationToken);
            if (failure == "bloat")
            {
                Directory.CreateDirectory(Path.Combine(spec.WorkingDirectory, "tmp"));
                using (var sparse = File.Create(Path.Combine(spec.WorkingDirectory, "tmp/bloat.bin"))) sparse.SetLength(2L * 1024 * 1024 * 1024 + 1);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var library = Path.Combine(spec.WorkingDirectory, "libraries/fixture/generated.jar"); Directory.CreateDirectory(Path.GetDirectoryName(library)!);
            if (failure == "link") File.CreateSymbolicLink(library, path);
            else await File.WriteAllBytesAsync(library, Fixture.Generated, cancellationToken);
            return new(0, "controlled fixture completed", "");
        }
    }
}
