using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

public sealed partial class AccountAppearanceTests
{
    [TestMethod]
    public async Task LegacyAccessOnlySessionCanReadItsPortraitWhileTheTokenStillWorks()
    {
        await using var fixture = await SessionFixture.CreateAsync(expired: true, accessOnly: true);
        Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true)).Skin);
        Assert.AreEqual(0, fixture.Refreshes); Assert.AreEqual(0, fixture.Credentials.Writes);
    }

    [TestMethod]
    public async Task RestoredYggdrasilSessionRenewsOnceAndPublicTextureRequestsNeverReceiveCredentials()
    {
        await using var fixture = await SessionFixture.CreateAsync(yggdrasil: true);
        Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync(Ygg().Id, true)).Skin);
        Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync(Ygg().Id, true)).Skin);
        Assert.AreEqual(1, fixture.Refreshes); Assert.AreEqual(1, fixture.Credentials.Writes); Assert.AreEqual(0, fixture.Credentials.Deletes);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task RestoredMicrosoftCredentialsRefreshOnceAndPreserveSelection(bool backgroundAccount)
    {
        await using var fixture = await SessionFixture.CreateAsync(expired: true, selectOffline: backgroundAccount);
        var selected = fixture.Service.Snapshot.SelectedAccountId;
        var results = await Task.WhenAll(fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true), fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true));
        Assert.IsTrue(results.All(r => r.Skin is not null)); Assert.AreEqual(1, fixture.Refreshes);
        Assert.AreEqual(selected, fixture.Service.Snapshot.SelectedAccountId); Assert.AreEqual("Ready", fixture.Service.Snapshot.State);
        Assert.AreEqual("renewed-minecraft", JsonSerializer.Deserialize<AuthSession>(fixture.Credentials.Bytes, ConfigurationStore.JsonOptions)!.AccessToken);
        Assert.IsTrue(fixture.Service.Snapshot.Accounts.Single(a => a.Id == "microsoft:fixture").ExpiresAt > DateTimeOffset.UtcNow);
        Assert.AreEqual(0, fixture.Credentials.Deletes);
    }

    [TestMethod]
    public async Task OfficialProfileUnauthorizedRefreshesOnceButTextureUnauthorizedDoesNotRefresh()
    {
        await using (var fixture = await SessionFixture.CreateAsync(rejectOldProfile: true))
        {
            Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true)).Skin);
            Assert.AreEqual(1, fixture.Refreshes);
        }
        await using (var fixture = await SessionFixture.CreateAsync(rejectTexture: true))
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true));
            Assert.AreEqual(0, fixture.Refreshes); Assert.AreEqual(0, fixture.Credentials.Writes);
        }
    }

    [TestMethod]
    public async Task InvalidRefreshKeepsOriginalCredentialsAndDiskRoster()
    {
        await using var fixture = await SessionFixture.CreateAsync(expired: true, invalidRefresh: true);
        var before = fixture.Credentials.Bytes.ToArray(); var config = await File.ReadAllBytesAsync(fixture.Config);
        var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true));
        Assert.AreEqual("invalid_grant", error.Code); Assert.AreEqual(1, fixture.Refreshes);
        Assert.AreEqual(0, fixture.Credentials.Writes); Assert.AreEqual(0, fixture.Credentials.Deletes);
        CollectionAssert.AreEqual(before, fixture.Credentials.Bytes); CollectionAssert.AreEqual(config, await File.ReadAllBytesAsync(fixture.Config));
        Assert.AreEqual("microsoft:fixture", fixture.Service.Snapshot.SelectedAccountId);
    }

    [TestMethod]
    public async Task DeniedCredentialWriteKeepsOriginalOnDiskAndUsesRenewedSessionInMemory()
    {
        await using var fixture = await SessionFixture.CreateAsync(expired: true, failWrite: true);
        var original = fixture.Credentials.Bytes.ToArray(); var config = await File.ReadAllTextAsync(fixture.Config);
        Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true)).Skin);
        Assert.IsFalse(fixture.Service.Snapshot.CanPersistAccounts); Assert.AreEqual(0, fixture.Credentials.Deletes);
        CollectionAssert.AreEqual(original, fixture.Credentials.Bytes);
        using var before = JsonDocument.Parse(config); using var after = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Config));
        Assert.AreEqual(before.RootElement.GetProperty("accounts").GetRawText(), after.RootElement.GetProperty("accounts").GetRawText());
        Assert.AreEqual(before.RootElement.GetProperty("selectedAccountId").GetString(), after.RootElement.GetProperty("selectedAccountId").GetString());
        Assert.IsNotNull((await fixture.Service.GetAccountAppearanceAsync("microsoft:fixture", true)).Skin); Assert.AreEqual(1, fixture.Refreshes);
    }

    private sealed class SessionCredentials(byte[] seed, bool failWrite) : ICredentialStore
    {
        public byte[] Bytes { get; private set; } = seed;
        public int Writes, Deletes;
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(Bytes.ToArray());
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            Writes++; if (failWrite) throw new CredentialStoreException(CredentialFailure.Denied, "Fixture write denied.");
            Bytes = value.ToArray(); return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Deletes++; return Task.CompletedTask; }
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        public required LauncherService Service;
        public required SessionCredentials Credentials;
        public required string Config;
        private HttpClient Http = null!;
        private ProcessRunner Processes = null!;
        private IDisposable Lease = null!;
        public int Refreshes;

        public static async Task<SessionFixture> CreateAsync(bool expired = false, bool selectOffline = false, bool rejectOldProfile = false, bool rejectTexture = false, bool invalidRefresh = false, bool failWrite = false, bool yggdrasil = false, bool accessOnly = false)
        {
            SessionFixture? fixture = null;
            var handler = new ScriptedHttpHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/authserver/refresh", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref fixture!.Refreshes);
                    return Task.FromResult(ScriptedHttpHandler.Json($$$$"""{"accessToken":"renewed-ygg","clientToken":"fixture-client","selectedProfile":{"id":"{{{{Uuid}}}}","name":"Fixture"}}"""));
                }
                if (uri.Host == "skin.example.org")
                {
                    Assert.IsNull(request.Headers.Authorization);
                    if (uri.AbsolutePath.EndsWith("/api/yggdrasil/", StringComparison.Ordinal)) return Task.FromResult(ScriptedHttpHandler.Json("{\"skinDomains\":[\"textures.minecraft.net\"]}"));
                    var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($$$$"""{"profileId":"{{{{Uuid}}}}","textures":{"SKIN":{"url":"https://textures.minecraft.net/texture/fixture"}}}"""));
                    return Task.FromResult(ScriptedHttpHandler.Json($$"""{"id":"{{Uuid}}","properties":[{"name":"textures","value":"{{payload}}"}]}"""));
                }
                if (uri.Host == "login.microsoftonline.com")
                {
                    Interlocked.Increment(ref fixture!.Refreshes);
                    return Task.FromResult(invalidRefresh ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") }
                        : ScriptedHttpHandler.Json("{\"access_token\":\"renewed-oauth\",\"refresh_token\":\"renewed-refresh\"}"));
                }
                if (uri.Host is "user.auth.xboxlive.com" or "xsts.auth.xboxlive.com") return Task.FromResult(ScriptedHttpHandler.Json("{\"Token\":\"fixture-xbox\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"fixture-hash\"}]}}"));
                if (uri.AbsolutePath == "/authentication/login_with_xbox") return Task.FromResult(ScriptedHttpHandler.Json("{\"access_token\":\"renewed-minecraft\",\"expires_in\":86400}"));
                if (uri.AbsolutePath == "/entitlements/mcstore") return Task.FromResult(ScriptedHttpHandler.Json("{\"items\":[{\"name\":\"game_minecraft\"}]}"));
                if (uri.AbsolutePath == "/minecraft/profile")
                {
                    if (rejectOldProfile && request.Headers.Authorization?.Parameter == "legacy-minecraft") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                    return Task.FromResult(ScriptedHttpHandler.Json($$"""{"id":"{{Uuid}}","name":"Fixture","skins":[{"state":"ACTIVE","variant":"CLASSIC","url":"https://textures.minecraft.net/texture/fixture"}],"capes":[]}"""));
                }
                Assert.AreEqual("textures.minecraft.net", uri.Host); Assert.IsNull(request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(rejectTexture ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) { Content = new ByteArrayContent(Png()) });
            });
            var http = new HttpClient(handler); var downloader = new VerifiedDownloader(http);
            var profile = (yggdrasil ? Ygg() : Microsoft()) with { Persisted = true, ExpiresAt = expired || yggdrasil ? null : DateTimeOffset.UtcNow.AddHours(1) };
            var credentials = new SessionCredentials(JsonSerializer.SerializeToUtf8Bytes(new AuthSession(profile, "legacy-minecraft", accessOnly ? null : "legacy-refresh", ClientToken: "fixture-client"), ConfigurationStore.JsonOptions), failWrite);
            var platform = TestPlatformServices.Create(new(TestFiles.Root()), downloader) with { Credentials = credentials };
            var lease = platform.DataLock.Acquire(platform.Paths);
            await using (var store = new ConfigurationStore(platform.Paths))
                await store.SaveAsync(new(new(MicrosoftClientId: "11111111-2222-3333-4444-555555555555", Proxy: new(ProxyMode.Direct)), [], selectOffline ? [profile, Offline()] : [profile], selectOffline ? Offline().Id : profile.Id));
            var service = new LauncherService(platform, http, downloader, new NetworkOptions(), accountAppearanceTransportFactory: () => new HttpClient(handler, disposeHandler: false));
            fixture = new() { Service = service, Credentials = credentials, Config = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json"), Http = http, Processes = (ProcessRunner)platform.Processes, Lease = lease };
            await service.InitializeAsync(); return fixture;
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Processes.DisposeAsync(); Lease.Dispose(); Http.Dispose(); }
    }
}
