using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class TransactionalInstallTests
{
    [TestMethod, DataRow("success", false), DataRow("cancel-download", false), DataRow("corrupt-download", false), DataRow("persist-failure", false),
        DataRow("success", true), DataRow("cancel-download", true), DataRow("corrupt-download", true), DataRow("persist-failure", true), DataRow("conflict", true)]
    public async Task CompleteInstallRegistersOnlyAfterFilesAndPersistenceSucceed(string scenario, bool shared)
    {
        var client = Encoding.UTF8.GetBytes("controlled client"); var resource = Encoding.UTF8.GetBytes("controlled resource");
        var assets = JsonSerializer.SerializeToUtf8Bytes(new { map_to_resources = true, objects = new Dictionary<string, object> { ["fixture/sample"] = new { hash = Convert.ToHexStringLower(SHA1.HashData(resource)), size = resource.Length } } });
        using var cancel = new CancellationTokenSource(); var downloadReached = false;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "meta.quiltmc.org") return ScriptedHttpHandler.Json("""{"id":"quilt-loader-0.30.2-1.21.1","inheritsFrom":"1.21.1","mainClass":"org.quiltmc.loader.impl.launch.knot.KnotClient","libraries":[],"arguments":{"game":[],"jvm":[]}}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/client"))
            {
                downloadReached = true;
                if (scenario == "cancel-download") { cancel.Cancel(); await Task.Delay(100, ct); }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario == "corrupt-download" ? [0] : client) };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/assets")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(assets) };
            if (request.RequestUri.AbsolutePath.EndsWith(Convert.ToHexStringLower(SHA1.HashData(resource)), StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(resource) };
            throw new AssertFailedException("Unexpected network request.");
        }));
        var downloader = new VerifiedDownloader(http); var original = TestPlatformServices.Create(new(TestFiles.Root()), downloader);
        await using var runner = (ProcessRunner)original.Processes;
        var platform = original with { JavaDiscovery = new Discovery(original.Info.Context.HardwareArchitecture), Processes = new NoGameProcesses() };
        using var lease = platform.DataLock.Acquire(platform.Paths);
        var folder = Path.Combine(platform.Paths.CacheDirectory, "versions/1.21.1"); Directory.CreateDirectory(folder);
        object Descriptor(string name, byte[] bytes) => new { url = "https://example.invalid/" + name, sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes)), size = bytes.Length };
        await File.WriteAllTextAsync(Path.Combine(folder, "1.21.1.json"), JsonSerializer.Serialize(new { id = "1.21.1", mainClass = "fixture.Main", javaVersion = new { majorVersion = 21, component = "java-runtime-delta" },
            libraries = Array.Empty<object>(), downloads = new { client = Descriptor("client", client) }, assetIndex = new { id = "fixture", url = "https://example.invalid/assets", sha1 = Convert.ToHexStringLower(SHA1.HashData(assets)), size = assets.Length } }));
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions()); await service.InitializeAsync();
        string? existingDirectory = null;
        if (shared)
        {
            await service.SaveSettingsAsync(service.Snapshot.Settings with { LaunchOptions = new(Isolation: InstanceIsolation.Shared) });
            existingDirectory = (await service.CreateInstanceAsync("已有共享实例", "1.21.1")).GameDirectory;
            Directory.CreateDirectory(Path.Combine(existingDirectory, "config")); await File.WriteAllTextAsync(Path.Combine(existingDirectory, "config/user.json"), "preserve user config");
            Directory.CreateDirectory(Path.Combine(existingDirectory, ".shou")); await File.WriteAllTextAsync(Path.Combine(existingDirectory, ".shou/install.json"), "original marker");
            if (scenario == "conflict") { Directory.CreateDirectory(Path.Combine(existingDirectory, "resources/fixture")); await File.WriteAllTextAsync(Path.Combine(existingDirectory, "resources/fixture/sample"), "preserve conflicting user file"); }
        }
        var before = service.Snapshot.Instances.ToArray(); var previousDirectories = Directory.GetDirectories(platform.Paths.InstancesDirectory).Order().ToArray();
        if (scenario == "persist-failure")
        {
            var state = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json");
            if (File.Exists(state)) File.Move(state, state + ".fixture-original"); Directory.CreateDirectory(state);
        }
        if (scenario == "success")
        {
            var instance = await service.CreateInstalledInstanceAsync("中文 完整安装", "1.21.1", "quilt", "0.30.2", cancel.Token);
            Assert.IsTrue(instance.Installed); Assert.IsTrue(downloadReached);
            Assert.IsTrue(File.Exists(Path.Combine(instance.GameDirectory, ".shou/install.json")));
            Assert.AreEqual(instance.Id, service.Snapshot.SelectedInstanceId);
            Assert.IsFalse(instance.GameDirectory.Contains(".shou-install-"));
            if (shared) Assert.AreEqual(existingDirectory, instance.GameDirectory);
        }
        else
        {
            try { await service.CreateInstalledInstanceAsync("失败副本", "1.21.1", "quilt", "0.30.2", cancel.Token); Assert.Fail("Controlled installation must fail."); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or AggregateException) { }
            Assert.IsTrue(downloadReached); CollectionAssert.AreEqual(before, service.Snapshot.Instances.ToArray());
            CollectionAssert.AreEqual(previousDirectories, Directory.GetDirectories(platform.Paths.InstancesDirectory).Order().ToArray());
            if (shared) Assert.AreEqual("original marker", await File.ReadAllTextAsync(Path.Combine(existingDirectory!, ".shou/install.json")));
        }
        if (shared) Assert.AreEqual("preserve user config", await File.ReadAllTextAsync(Path.Combine(existingDirectory!, "config/user.json")));
        Assert.IsEmpty(Directory.GetDirectories(platform.Paths.InstancesDirectory, ".shou-install-*"));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class Discovery(RuntimeArchitecture architecture) : IJavaDiscovery
    {
        private JavaInstallation Java => new("/controlled/java", "/controlled", "21", 21, "fixture", architecture, "fixture");
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(Java);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JavaInstallation>>([Java]);
    }
    private sealed class NoGameProcesses : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("Game start forbidden in R18.");
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("No process needed for metadata-only Quilt install.");
    }
}
