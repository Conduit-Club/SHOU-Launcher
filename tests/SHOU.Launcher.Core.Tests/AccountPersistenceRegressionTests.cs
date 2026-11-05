using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass, TestCategory("DesktopPlatforms")]
public sealed class AccountPersistenceRegressionTests
{
    [TestMethod]
    public async Task ReadFailurePreservesOriginalAccountsAndNewOfflineIsSessionOnly()
    {
        await using var fixture = await Fixture.CreateAsync(seedAccount: true, failRead: true);
        var before = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Config))!;
        Assert.IsFalse(fixture.Service.Snapshot.CanPersistAccounts);
        var account = await fixture.Service.AddOfflineAccountAsync("SessionOnly");
        Assert.IsFalse(account.Persisted);
        Assert.IsFalse(fixture.Service.Snapshot.Accounts.Single(a => a.Id == account.Id).Persisted);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Config))!;
        Assert.IsTrue(JsonNode.DeepEquals(before["accounts"], after["accounts"]));
        Assert.AreEqual((string?)before["selectedAccountId"], (string?)after["selectedAccountId"]);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.RemoveAccountAsync("existing"));
        Assert.AreEqual(0, fixture.Credentials.Writes);
        Assert.AreEqual(0, fixture.Credentials.Deletes);
    }

    [TestMethod]
    public async Task FailedOfflineSidecarCommitDoesNotPublishSavedDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        Directory.CreateDirectory(fixture.Config);
        await TestFiles.AssertDirectoryCommitFailureAsync(() => fixture.Service.AddOfflineAccountAsync("UnsavedDraft"));
        Assert.AreEqual(0, fixture.Service.Snapshot.Accounts.Count);
        Assert.IsNull(fixture.Service.Snapshot.SelectedAccountId);
    }

    [TestMethod]
    public async Task FailedOnlineSidecarCommitDoesNotPublishSavedAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        Directory.CreateDirectory(fixture.Config);
        await TestFiles.AssertDirectoryCommitFailureAsync(() => fixture.Service.LoginYggdrasilAsync(new("http://127.0.0.1/ygg"), "fixture", "fixture-only"));
        Assert.AreEqual(1, fixture.Credentials.Writes, "Protected write happened; a sidecar failure must still not publish saved UI state.");
        Assert.AreEqual(0, fixture.Service.Snapshot.Accounts.Count);
        Assert.IsNull(fixture.Service.Snapshot.SelectedAccountId);
        Assert.AreEqual("Error", fixture.Service.Snapshot.State);
    }

    [TestMethod]
    public async Task FailedCredentialWriteKeepsSessionOnlyWithoutDanglingSavedSelection()
    {
        await using var fixture = await Fixture.CreateAsync(failWrite: true);
        var result = await fixture.Service.LoginYggdrasilAsync(new("http://127.0.0.1/ygg"), "fixture", "fixture-only");
        Assert.AreEqual(1, result.Count);
        Assert.IsFalse(result[0].Persisted);
        Assert.IsFalse(fixture.Service.Snapshot.CanPersistAccounts);
        Assert.IsFalse(fixture.Service.Snapshot.Accounts.Single().Persisted);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Config))!;
        Assert.IsTrue(state["accounts"] is null || state["accounts"]!.AsArray().Count == 0);
        Assert.IsNull(state["selectedAccountId"], "Session-only role selection must not replace the disk selection.");
    }

    [TestMethod]
    public async Task SidecarFailureAfterRefreshKeepsRotatedSessionAndOriginalDiskAccounts()
    {
        await using var fixture = await Fixture.CreateAsync(seedAccount: true);
        var original = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Config))!;
        File.Move(fixture.Config, fixture.Config + ".preserved-fixture");
        Directory.CreateDirectory(fixture.Config);
        await TestFiles.AssertDirectoryCommitFailureAsync(() => fixture.Service.RefreshAccountAsync("existing"));
        Assert.AreEqual("initial-fixture-token", fixture.SeenTokens.Single());
        Assert.IsFalse(fixture.Service.Snapshot.CanPersistAccounts);
        Assert.IsFalse(fixture.Service.Snapshot.Accounts.Single().Persisted);
        Directory.Move(fixture.Config, fixture.Config + ".failed-write-directory");
        File.Move(fixture.Config + ".preserved-fixture", fixture.Config);
        await fixture.Service.RefreshAccountAsync("existing");
        CollectionAssert.AreEqual(new[] { "initial-fixture-token", "fixture-token-1" }, fixture.SeenTokens);
        Assert.AreEqual(1, fixture.Credentials.Writes, "Account persistence stays disabled after the failed sidecar commit.");
        var after = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Config))!;
        Assert.IsTrue(JsonNode.DeepEquals(original["accounts"], after["accounts"]));
        Assert.AreEqual((string?)original["selectedAccountId"], (string?)after["selectedAccountId"]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly HttpClient http;
        private readonly ProcessRunner processes;
        private readonly IDisposable lease;
        public LauncherService Service { get; }
        public FixtureCredentials Credentials { get; }
        public string Config { get; }
        public List<string> SeenTokens { get; }
        private Fixture(LauncherService service, FixtureCredentials credentials, string config, HttpClient http, ProcessRunner processes, IDisposable lease, List<string> seenTokens)
        { Service = service; Credentials = credentials; Config = config; this.http = http; this.processes = processes; this.lease = lease; SeenTokens = seenTokens; }
        public static async Task<Fixture> CreateAsync(bool seedAccount = false, bool failRead = false, bool failWrite = false)
        {
            var seenTokens = new List<string>(); var replies = 0;
            var http = new HttpClient(new ScriptedHttpHandler(async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("refresh", StringComparison.Ordinal))
                    seenTokens.Add((string)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["accessToken"]!);
                return ScriptedHttpHandler.Json($$$"""{"accessToken":"fixture-token-{{{++replies}}}","clientToken":"fixture-client","selectedProfile":{"id":"11111111222233334444555555555555","name":"FixtureUser"}}""");
            }));
            var downloads = new VerifiedDownloader(http);
            var original = PlatformServicesFactory.Create(new(TestFiles.Root()), downloads);
            var seed = new AccountProfile("existing", AccountKind.Yggdrasil, "FixtureUser", "11111111222233334444555555555555", "http://127.0.0.1/ygg", Persisted: true);
            var credentials = new FixtureCredentials(failRead, failWrite, seedAccount ? new(seed, "initial-fixture-token", ClientToken: "fixture-client") : null);
            var platform = original with { Credentials = credentials };
            var lease = platform.DataLock.Acquire(platform.Paths);
            if (seedAccount)
            {
                await using var config = new ConfigurationStore(platform.Paths);
                await config.SaveAsync(new(new(), [], [seed], "existing"));
            }
            var service = new LauncherService(platform, http, downloads, new NetworkOptions());
            await service.InitializeAsync();
            return new(service, credentials, Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json"), http, (ProcessRunner)platform.Processes, lease, seenTokens);
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); await processes.DisposeAsync(); lease.Dispose(); http.Dispose(); }
    }

    private sealed class FixtureCredentials(bool failRead, bool failWrite, AuthSession? seed) : ICredentialStore
    {
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failRead) throw new CredentialStoreException(CredentialFailure.Denied, "Synthetic denied credential; no native or real account access.");
            return Task.FromResult(seed is null ? null : JsonSerializer.SerializeToUtf8Bytes(seed, ConfigurationStore.JsonOptions));
        }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Writes++;
            if (failWrite) throw new CredentialStoreException(CredentialFailure.Denied, "Synthetic denied write.");
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Deletes++; return Task.CompletedTask; }
    }
}
