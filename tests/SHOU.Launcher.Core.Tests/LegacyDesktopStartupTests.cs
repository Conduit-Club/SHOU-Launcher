using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LegacyDesktopStartupTests
{
    private const string Uuid = "11111111222233334444555555555555";
    private const string Secret = "synthetic-legacy-startup-secret";

    [TestMethod]
    public async Task ReaderDiscoversColocatedAndConfiguredFoldersAndKeepsOriginalFiles()
    {
        var root = TestFiles.Root(); var exe = Path.Combine(root, "旧启动器 中文"); var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared); Directory.CreateDirectory(Path.Combine(exe, "PCL"));
        var minecraft = Path.Combine(exe, ".minecraft"); var folder = LegacyInstanceManagementTests.Profile(minecraft, "Fixture", "net.fabricmc:fabric-loader:0.16.14");
        var external = Path.Combine(root, "其它 游戏"); LegacyInstanceManagementTests.Profile(external, "Other");
        var config = Path.Combine(shared, "config.v1.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { Profile = "encrypted placeholder", LaunchFolders = "other>" + external + Path.DirectorySeparatorChar }));
        await File.WriteAllTextAsync(Path.Combine(exe, "PCL", "config.v1.yml"), "LaunchFolderSelect: " + JsonSerializer.Serialize(minecraft) + "\nLaunchInstanceSelect: Fixture\n");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(config));
        var body = JsonSerializer.Serialize(new { lastUsed = 1, profiles = new[] {
            new { profileType = "Offline", provider = "", username = "Local", uuid = Uuid, accessToken = "", refreshToken = "", clientToken = "", server = "" },
            new { profileType = "Microsoft", provider = "microsoft", username = "Online", uuid = Uuid, accessToken = Secret, refreshToken = Secret, clientToken = "", server = "" },
            new { profileType = "Authlib", provider = "", username = "Skin", uuid = Uuid, accessToken = Secret, refreshToken = "", clientToken = Secret, server = "https://skin.example/api/yggdrasil" } } });
        var data = await new WindowsLegacyDesktopDataReader(exe, shared, (_, _) => Task.FromResult(body)).ReadAsync();
        Assert.HasCount(2, data.GameFolders); Assert.HasCount(3, data.Accounts); Assert.AreEqual("microsoft:" + Uuid, data.SelectedAccountId);
        Assert.AreEqual(folder, data.SelectedVersionDirectory); Assert.IsFalse(JsonSerializer.Serialize(data).Contains(Secret));
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(config)));
    }

    [TestMethod]
    public async Task UnreadableLegacyCredentialsLeaveInstancesDiscoverableAndNeverInventLogin()
    {
        var root = TestFiles.Root(); Directory.CreateDirectory(Path.Combine(root, ".minecraft", "versions"));
        var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(shared);
        await File.WriteAllTextAsync(Path.Combine(shared, "config.v1.json"), "{\"Profile\":\"encrypted\"}");
        var data = await new WindowsLegacyDesktopDataReader(root, shared, (_, _) => throw new CredentialStoreException(CredentialFailure.Corrupt, Secret)).ReadAsync();
        Assert.HasCount(1, data.GameFolders); Assert.IsEmpty(data.Accounts); Assert.IsTrue(data.Issues.Contains("legacy_credentials:Corrupt"));
        Assert.IsFalse(string.Join(',', data.Issues).Contains(Secret));
    }

    [TestMethod]
    public void ExistingPreviewAndProductionConfigurationsKeepTheirOriginalDataRoot()
    {
        var root = TestFiles.Root(); var exe = Path.Combine(root, "app"); Directory.CreateDirectory(exe);
        var local = Path.Combine(root, "local"); var preview = Path.Combine(local, "SHOU-Launcher-Preview");
        Directory.CreateDirectory(Path.Combine(preview, "local")); File.WriteAllText(Path.Combine(preview, "local", "shou.desktop.v1.json"), "{}");
        Assert.AreEqual(preview, WindowsStartupPaths.ResolveDataDirectory(exe, local));
        Directory.CreateDirectory(Path.Combine(exe, "PCL")); Assert.AreEqual(preview, WindowsStartupPaths.ResolveDataDirectory(exe, local), "An update beside old WPF must not abandon the current desktop state or protected store.");
        Directory.CreateDirectory(Path.Combine(local, "PCLCE")); File.WriteAllText(Path.Combine(local, "PCLCE", "shou.desktop.v1.json"), "{}");
        Assert.IsNull(WindowsStartupPaths.ResolveDataDirectory(exe, local));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task FirstRunCommitsProtectedCredentialsAndMetadataOnceAndReloadDoesNotReimportRemovedData()
    {
        await using var f = new Fixture(); var old = LegacyInstanceManagementTests.Profile(f.Source, "Fixture");
        f.Data = new([f.Source], [new(new("microsoft:" + Uuid, AccountKind.Microsoft, "Fixture", Uuid), Secret, Secret)], "microsoft:" + Uuid, old);
        await f.StartAsync(); Assert.HasCount(1, f.Service.Snapshot.Instances); Assert.HasCount(1, f.Service.Snapshot.Accounts);
        Assert.IsTrue(f.Service.Snapshot.Accounts.Single().Persisted); Assert.AreEqual(1, f.Writes);
        Assert.IsFalse((await File.ReadAllTextAsync(f.Config)).Contains(Secret));
        await f.Service.RemoveAccountAsync("microsoft:" + Uuid); var before = await File.ReadAllBytesAsync(f.Config);
        await f.StartAsync(); Assert.IsEmpty(f.Service.Snapshot.Accounts); Assert.HasCount(1, f.Service.Snapshot.Instances); Assert.AreEqual(1, f.Reads);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(f.Config));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task FailedSidecarCommitRollsBackOnlyNewCredentialRecordsAndNeverModifiesLegacyFiles()
    {
        await using var f = new Fixture(); f.Data = new([], [new(new("microsoft:" + Uuid, AccountKind.Microsoft, "Fixture", Uuid), Secret, Secret)]);
        f.AfterWrite = () => Directory.CreateDirectory(f.Config); // Controlled publish failure after DPAPI-equivalent write.
        var error = await Assert.ThrowsAsync<Exception>(() => f.StartAsync());
        Assert.IsTrue(error is IOException or UnauthorizedAccessException); Assert.IsEmpty(f.Stored); Assert.AreEqual(1, f.Deletes);
        Assert.IsFalse(File.Exists(f.Config)); Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(f.Config)!).Any(p => p.Contains(".tmp-")));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task CancelAfterProtectedWriteRollsBackBeforeMetadataPublication()
    {
        await using var f = new Fixture(); f.Data = new([], [new(new("microsoft:" + Uuid, AccountKind.Microsoft, "Fixture", Uuid), Secret, Secret)]);
        using var stop = new CancellationTokenSource(); f.AfterWrite = stop.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.StartAsync(stop.Token)); Assert.IsEmpty(f.Stored); Assert.IsFalse(File.Exists(f.Config));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task ExistingNewVersionCredentialIsNeverOverwrittenByAnOlderCachedSession()
    {
        await using var f = new Fixture(); var account = new AccountProfile("microsoft:" + Uuid, AccountKind.Microsoft, "Fixture", Uuid);
        var key = "account:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Id)));
        var current = JsonSerializer.SerializeToUtf8Bytes(new AuthSession(account, "synthetic-current-access", "synthetic-current-refresh"), ConfigurationStore.JsonOptions);
        f.Stored[key] = current.ToArray(); f.Data = new([], [new(account, Secret, Secret)]);
        await f.StartAsync(); Assert.AreEqual(0, f.Writes); CollectionAssert.AreEqual(current, f.Stored[key]);
        Assert.IsTrue(f.Service.Snapshot.Accounts.Single().Persisted);
    }

    private sealed class Fixture : IAsyncDisposable, ICredentialStore, ILegacyDesktopDataReader
    {
        private readonly string root = TestFiles.Root(); private readonly HttpClient http = new(new NeverNetwork());
        public string Source => Path.Combine(root, "old");
        private PlatformServices? platform;
        public LauncherService Service { get; private set; } = null!;
        public string Config => Path.Combine(platform!.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        public LegacyDesktopData Data { get; set; } = new([], []);
        public Dictionary<string, byte[]> Stored { get; } = []; public int Writes, Reads, Deletes; public Action? AfterWrite;
        public async Task StartAsync(CancellationToken ct = default)
        {
            if (Service is not null) await Service.DisposeAsync();
            platform ??= TestPlatformServices.Create(new(Path.Combine(root, "new")), new VerifiedDownloader(http)) with { Credentials = this, LegacyDesktopData = this };
            Service = new(platform, http, new VerifiedDownloader(http), new()); await Service.InitializeAsync(ct);
        }
        public Task<LegacyDesktopData> ReadAsync(CancellationToken ct = default) { Reads++; return Task.FromResult(Data); }
        public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult(Stored.TryGetValue(key, out var b) ? b.ToArray() : null);
        public Task WriteAsync(string key, ReadOnlyMemory<byte> b, CancellationToken ct = default) { Stored[key] = b.ToArray(); Writes++; AfterWrite?.Invoke(); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Stored.Remove(key); Deletes++; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() { if (Service is not null) await Service.DisposeAsync(); http.Dispose(); }
    }
    private sealed class NeverNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => throw new AssertFailedException("Startup migration must not contact authentication or start Minecraft."); }
}
