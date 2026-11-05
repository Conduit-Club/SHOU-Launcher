using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class LegacyMicrosoftAccountMigrationTests
{
    private const string Uuid = "11111111222233334444555555555555";
    private const string Secret = "synthetic-legacy-refresh-never-log";

    [TestMethod]
    public async Task ReaderIsBoundedReadonlyAndNeverReturnsOtherProvidersOrSerializesTokens()
    {
        var directory = TestFiles.Root(); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "config.v1.json");
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"Profile":"synthetic-envelope","unknownHistory":{"EasyTier":"preserve"}}""")).ToArray();
        await File.WriteAllBytesAsync(file, bytes);
        var body = JsonSerializer.Serialize(new { profiles = new object[] {
            new {profileId="chosen",username="OldFixture",uuid=Uuid,profileType="Microsoft",provider="microsoft",refreshToken=Secret},
            new {profileId="thirdparty",username="Other",uuid=Uuid,profileType="Microsoft",provider="other",refreshToken="synthetic-other"},
            new {profileId="offline",username="Offline",uuid=Uuid,profileType="Offline",provider="offline",refreshToken=""}
        }});
        var reads = 0;
        var reader = new WindowsLegacyMicrosoftAccountReader(directory, (value, ct) =>
        { ct.ThrowIfCancellationRequested(); Assert.AreEqual("synthetic-envelope", value); reads++; return Task.FromResult(body); });
        var result = await reader.ReadAsync();
        Assert.AreEqual(1, result.Count); Assert.AreEqual("OldFixture", result[0].DisplayName);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(result[0].ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.AreEqual(result[0].SourceId, (await reader.ReadAsync()).Single().SourceId);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file));
        Assert.AreEqual(2, reads); Assert.AreEqual(1, Directory.GetFiles(directory).Length);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(cancelled.Token));
    }

    [TestMethod]
    public async Task CorruptOversizedAndAmbiguousSourcesFailWithoutEchoOrWrites()
    {
        var directory = TestFiles.Root(); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "config.v1.json");
        var source = JsonSerializer.Serialize(new { Profile = "synthetic-envelope" });
        await File.WriteAllTextAsync(file, source);
        var entry = new { profileId = "same", username = "Fixture", uuid = Uuid, profileType = "Microsoft", provider = "microsoft", refreshToken = Secret };
        foreach (var body in new[] { "{" + Secret, JsonSerializer.Serialize(new { profiles = new[] { entry, entry } }), JsonSerializer.Serialize(new { profiles = new[] { entry with { uuid = "bad-uuid" } } }) })
        {
            var reader = new WindowsLegacyMicrosoftAccountReader(directory, (_, _) => Task.FromResult(body));
            var error = await Assert.ThrowsExactlyAsync<CredentialStoreException>(() => reader.ReadAsync());
            Assert.AreEqual(CredentialFailure.Corrupt, error.Failure);
            Assert.IsFalse(error.ToString().Contains(Secret, StringComparison.Ordinal));
            Assert.AreEqual(source, await File.ReadAllTextAsync(file));
        }
        await File.WriteAllBytesAsync(file, new byte[2_000_001]);
        var calls = 0;
        var oversized = new WindowsLegacyMicrosoftAccountReader(directory, (_, _) => { calls++; return Task.FromResult("{}"); });
        await Assert.ThrowsExactlyAsync<CredentialStoreException>(() => oversized.ReadAsync());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [TestCategory("DesktopPlatforms")]
    public async Task ImportRequiresFreshOwnershipAndExactProfileBeforeProtectedSaveAndReload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var choices = await fixture.Service.ReadLegacyMicrosoftAccountsAsync();
        Assert.AreEqual(1, choices.Count); Assert.AreEqual(0, fixture.Writes);
        var account = await fixture.Service.ImportLegacyMicrosoftAccountAsync(choices.Single().SourceId);
        Assert.IsTrue(account.Persisted); Assert.AreEqual("VerifiedFixture", account.DisplayName);
        Assert.AreEqual(2, fixture.SourceReads, "Import rereads the source after the preview.");
        Assert.AreEqual(1, fixture.Writes); Assert.AreEqual(1, fixture.ProfileReads);
        var sidecar = await File.ReadAllTextAsync(fixture.Config);
        Assert.IsFalse(sidecar.Contains(Secret, StringComparison.Ordinal));
        Assert.IsFalse(sidecar.Contains("synthetic-minecraft", StringComparison.Ordinal));
        await fixture.Service.RefreshAccountAsync(account.Id);
        Assert.AreEqual(2, fixture.Writes);
        await fixture.ReloadAsync();
        Assert.AreEqual(account.Id, fixture.Service.Snapshot.SelectedAccountId);
        Assert.IsTrue(fixture.Service.Snapshot.Accounts.Single().Persisted);
        await fixture.Service.RefreshAccountAsync(account.Id);
        Assert.AreEqual(3, fixture.Writes);
        Assert.IsFalse(string.Join('\n', fixture.Service.GetRecentLogs()).Contains(Secret, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("not_owned", "minecraft_not_owned")]
    [DataRow("other_profile", "profile_mismatch")]
    [DataRow("invalid_grant", "invalid_grant")]
    [DataRow("changed", "legacy_source_changed")]
    [TestCategory("DesktopPlatforms")]
    public async Task RejectedImportNeverPersistsOrPublishesAnAccount(string mode, string expected)
    {
        await using var fixture = await Fixture.CreateAsync(mode);
        var choice = (await fixture.Service.ReadLegacyMicrosoftAccountsAsync()).Single();
        var before = await File.ReadAllBytesAsync(fixture.Config);
        var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => fixture.Service.ImportLegacyMicrosoftAccountAsync(choice.SourceId));
        Assert.AreEqual(expected, error.Code); Assert.AreEqual(0, fixture.Writes);
        Assert.AreEqual(0, fixture.Service.Snapshot.Accounts.Count);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Config));
        Assert.IsFalse(error.ToString().Contains(Secret, StringComparison.Ordinal));
    }

    [TestMethod, TestCategory("DesktopPlatforms")]
    public async Task CancelledImportDoesNotSaveOrChangeSelection()
    {
        await using var fixture = await Fixture.CreateAsync("cancel");
        var choice = (await fixture.Service.ReadLegacyMicrosoftAccountsAsync()).Single();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.ImportLegacyMicrosoftAccountAsync(choice.SourceId, cancel.Token));
        Assert.AreEqual(0, fixture.Writes); Assert.AreEqual(0, fixture.Service.Snapshot.Accounts.Count);
        Assert.IsNull(fixture.Service.Snapshot.SelectedAccountId);
    }

    private sealed class Fixture(string mode) : IAsyncDisposable, ILegacyMicrosoftAccountReader, ICredentialStore
    {
        public LauncherService Service { get; private set; } = null!;
        private PlatformServices platform = null!;
        private HttpClient http = null!;
        private IDisposable lease = null!;
        public string Config => Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json");
        public int Writes { get; private set; }
        public int SourceReads { get; private set; }
        public int ProfileReads { get; private set; }
        private byte[]? stored;
        public static async Task<Fixture> CreateAsync(string mode = "success")
        {
            var f = new Fixture(mode);
            f.http = new HttpClient(new ScriptedHttpHandler(f.ReplyAsync));
            var downloads = new VerifiedDownloader(f.http);
            f.platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloads) with { Credentials = f, LegacyMicrosoftAccounts = f };
            f.lease = f.platform.DataLock.Acquire(f.platform.Paths);
            f.Service = new(f.platform, f.http, downloads, new(), authenticationClient: new(f.http,
                new(new("http://127.0.0.1/device"), new("http://127.0.0.1/token"), new("http://127.0.0.1/xbl"), new("http://127.0.0.1/xsts"), new("http://127.0.0.1/mc"), new("http://127.0.0.1/entitlements"), new("http://127.0.0.1/profile"))));
            await f.Service.InitializeAsync();
            await f.Service.SaveSettingsAsync(f.Service.Snapshot.Settings with { MicrosoftClientId = "11111111-2222-3333-4444-555555555555" });
            return f;
        }
        public async Task ReloadAsync()
        {
            await Service.DisposeAsync();
            Service = new(platform, http, new VerifiedDownloader(http), new(), authenticationClient: new(http,
                new(new("http://127.0.0.1/device"), new("http://127.0.0.1/token"), new("http://127.0.0.1/xbl"), new("http://127.0.0.1/xsts"), new("http://127.0.0.1/mc"), new("http://127.0.0.1/entitlements"), new("http://127.0.0.1/profile"))));
            await Service.InitializeAsync();
        }
        public Task<IReadOnlyList<LegacyMicrosoftAccountCredential>> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); SourceReads++;
            return Task.FromResult<IReadOnlyList<LegacyMicrosoftAccountCredential>>(mode == "changed" && SourceReads > 1 ? [] : [new("chosen", "OldFixture", Uuid, Secret)]);
        }
        private async Task<HttpResponseMessage> ReplyAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (mode == "cancel") { await Task.Delay(250); throw new OperationCanceledException(); }
            if (path == "/token" && mode == "invalid_grant") return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant","error_description":"synthetic-legacy-refresh-never-log"}""") };
            if (path == "/profile") ProfileReads++;
            return path switch
            {
                "/token" => ScriptedHttpHandler.Json("""{"access_token":"synthetic-oauth","refresh_token":"synthetic-rotated-refresh"}"""),
                "/xbl" => ScriptedHttpHandler.Json("""{"Token":"synthetic-xbl"}"""),
                "/xsts" => ScriptedHttpHandler.Json("""{"Token":"synthetic-xsts","DisplayClaims":{"xui":[{"uhs":"synthetic-hash"}]}}"""),
                "/mc" => ScriptedHttpHandler.Json("""{"access_token":"synthetic-minecraft","expires_in":3600}"""),
                "/entitlements" => ScriptedHttpHandler.Json(mode == "not_owned" ? """{"items":[]}""" : """{"items":[{"name":"game_minecraft"}]}"""),
                "/profile" => ScriptedHttpHandler.Json(JsonSerializer.Serialize(new { id = mode == "other_profile" ? "22222222222233334444555555555555" : Uuid, name = "VerifiedFixture" })),
                _ => throw new InvalidOperationException("Unexpected synthetic endpoint")
            };
        }
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(stored?.ToArray());
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; stored = value.ToArray(); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No delete expected");
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); await ((ProcessRunner)platform.Processes).DisposeAsync(); lease.Dispose(); http.Dispose(); }
    }
}
