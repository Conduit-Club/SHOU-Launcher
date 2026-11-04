using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class ProcessLifecycleRegressionTests
{
    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task AlreadyExitedGameFailureSurvivesLaunchOperationCompletion()
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Cached fixture must not perform network requests.")));
        var downloader = new PipelineDownloads();
        var original = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        await using var nativeProcesses = (ProcessRunner)original.Processes;
        var platform = original with { Processes = new ImmediateFailureRunner(), JavaDiscovery = new FixtureJava(original.Info.Context.HardwareArchitecture) };
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var service = new LauncherService(platform, http, downloader, new NetworkOptions());
        await service.InitializeAsync();
        var metadataFile = Path.Combine(platform.Paths.CacheDirectory, "versions", "fixture", "fixture.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataFile)!);
        await File.WriteAllTextAsync(metadataFile, $$$"""
            {"id":"fixture","type":"release","mainClass":"fixture.Main","javaVersion":{"majorVersion":21},"libraries":[],
             "downloads":{"client":{"url":"https://example.invalid/client","sha1":"{{{PipelineDownloads.Hash(PipelineDownloads.Client)}}}","size":{{{PipelineDownloads.Client.Length}}}}},
             "assetIndex":{"id":"fixture-assets","url":"https://example.invalid/assets","sha1":"{{{PipelineDownloads.Hash(PipelineDownloads.Assets)}}}","size":{{{PipelineDownloads.Assets.Length}}}},
             "arguments":{"jvm":["-cp","${classpath}"],"game":["--username","${auth_player_name}"]}}
            """);
        var account = await service.AddOfflineAccountAsync("FixtureOnly");
        await service.SelectAccountAsync(account.Id);
        var instance = await service.CreateInstanceAsync("Exit fixture", "fixture");
        await service.LaunchAsync(instance.Id);
        Assert.AreEqual("Error", service.Snapshot.State);
        Assert.IsNotNull(service.Snapshot.Error);
        Assert.IsNull(service.Snapshot.GameProcessId);
        Assert.IsTrue(service.GetRecentLogs().Any(log => log.Message.Contains("code=17", StringComparison.Ordinal)));
    }

    private sealed class PipelineDownloads : IVerifiedDownloader
    {
        internal static readonly byte[] Client = Encoding.UTF8.GetBytes("fixture-client");
        internal static readonly byte[] Assets = Encoding.UTF8.GetBytes("{\"objects\":{}}");
        internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
        public async Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var bytes = request.DestinationPath.EndsWith("fixture-assets.json", StringComparison.Ordinal) ? Assets : Client;
            Assert.AreEqual(request.Sha1, Hash(bytes));
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, bytes, cancellationToken);
        }
    }
    private sealed class FixtureJava(RuntimeArchitecture architecture) : IJavaDiscovery
    {
        private JavaInstallation Java => new("/fixture/java", "/fixture", "21.0.13", 21, "fixture", architecture, "fixture");
        public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(Java);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JavaInstallation>>([Java]);
    }
    private sealed class ImmediateFailureRunner : IProcessRunner
    {
        public Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => Task.FromResult<IManagedProcess>(new FailedProcess());
        public Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("No helper process is expected.");
    }
    private sealed class FailedProcess : IManagedProcess
    {
        public int ProcessId => 17017;
        public Task<ProcessResult> Completion => Task.FromResult(new ProcessResult(17, "", "fixture failure"));
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
