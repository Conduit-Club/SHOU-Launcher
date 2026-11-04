using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class AuthRegressionTests
{
    private const string One = "11111111222233334444555555555555", Two = "aaaaaaaa222233334444555555555555";
    [TestMethod] public async Task UnboundLoginSelectsOneProfileAndRefreshesTokenExactlyOnce()
    {
        var requests = new List<JsonObject>();
        using var handler = new ScriptedHttpHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            if (request.RequestUri!.AbsolutePath.EndsWith("authenticate", StringComparison.Ordinal))
                return ScriptedHttpHandler.Json($$$"""{"accessToken":"unbound-token","clientToken":"fixture-client","availableProfiles":[{"id":"{{{One}}}","name":"First"},{"id":"{{{Two}}}","name":"Second"}]}""");
            requests.Add(body);
            return ScriptedHttpHandler.Json($$$"""{"accessToken":"bound-new-token","clientToken":"fixture-client","selectedProfile":{"id":"{{{Two}}}","name":"Second"}}""");
        });
        using var http = new HttpClient(handler); var client = new AuthenticationClient(http); var chosen = 0;
        var result = await client.LoginYggdrasilAsync(new Uri("http://127.0.0.1/ygg"), "fixture-user", "fixture-password", selectProfile: (choices, _) => { chosen++; Assert.AreEqual(2, choices.Count); return Task.FromResult<string?>(Two); });
        Assert.AreEqual(1, chosen); Assert.AreEqual(1, requests.Count); Assert.AreEqual(1, result.Count);
        Assert.AreEqual(Two, result[0].Profile.PlayerUuid); Assert.AreEqual("bound-new-token", result[0].AccessToken);
        Assert.AreEqual("unbound-token", (string?)requests[0]["accessToken"]); Assert.AreEqual(Two, (string?)requests[0]["selectedProfile"]?["id"]);
    }
    [TestMethod] public async Task BoundLoginDoesNotInventSessionsForOtherAvailableProfiles()
    {
        var calls = 0;
        using var handler = new ScriptedHttpHandler(_ => { calls++; return Task.FromResult(ScriptedHttpHandler.Json($$$"""{"accessToken":"bound-token","clientToken":"fixture","selectedProfile":{"id":"{{{One}}}","name":"First"},"availableProfiles":[{"id":"{{{One}}}","name":"First"},{"id":"{{{Two}}}","name":"Second"}]}""")); });
        using var http = new HttpClient(handler); var result = await new AuthenticationClient(http).LoginYggdrasilAsync(new Uri("http://127.0.0.1/ygg"), "fixture", "password");
        Assert.AreEqual(1, calls); Assert.AreEqual(1, result.Count); Assert.AreEqual(One, result[0].Profile.PlayerUuid);
    }
    [TestMethod] public async Task OrdinaryRefreshOmitsRoleSelectionAndKeepsIdentity()
    {
        JsonObject? body = null;
        using var handler = new ScriptedHttpHandler(async request => { body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject(); return ScriptedHttpHandler.Json($$$"""{"accessToken":"refreshed","clientToken":"fixture","selectedProfile":{"id":"{{{One}}}","name":"First"}}"""); });
        using var http = new HttpClient(handler);
        var profile = new AccountProfile("stable-id", AccountKind.Yggdrasil, "First", One, "http://127.0.0.1/ygg");
        var result = await new AuthenticationClient(http).RefreshYggdrasilAsync(new(profile, "bound-token", ClientToken: "fixture"));
        Assert.IsNotNull(body); Assert.IsNull(body["selectedProfile"]); Assert.AreEqual("refreshed", result.AccessToken); Assert.AreEqual(One, result.Profile.PlayerUuid);
    }
    [TestMethod] public void SidecarCannotRedirectProtectedCredentialsOrChangeAccountKind()
    {
        var original = new AccountProfile("id", AccountKind.Yggdrasil, "First", One, "https://skin.mualliance.ltd/api/union/yggdrasil");
        Assert.IsTrue(AccountIdentity.Matches(original, original));
        Assert.IsFalse(AccountIdentity.Matches(original with { AuthenticationServer = "https://attacker.invalid/yggdrasil" }, original));
        Assert.IsFalse(AccountIdentity.Matches(original with { Kind = AccountKind.Microsoft }, original));
        Assert.IsFalse(AccountIdentity.Matches(original with { PlayerUuid = Two }, original));
        Assert.IsFalse(AccountIdentity.Matches(original with { AuthenticationServer = "https://skin.mualliance.ltd/api/yggdrasil" }, original));
    }
    [TestMethod] public void DownloadTaskOwnershipSurvivesNewSelectionAndRejectsStaleCancellation()
    {
        var registry = new DownloadTaskRegistry(); var first = registry.Begin("instance-A"); var old = registry.Register("same-library.jar"); registry.End(first);
        var second = registry.Begin("instance-B"); var current = registry.Register("same-library.jar");
        Assert.AreEqual("instance-A", registry.Resolve(old).InstanceId); Assert.AreEqual("instance-B", registry.Resolve(current).InstanceId);
        Assert.AreNotEqual(old, current); Assert.ThrowsExactly<InvalidOperationException>(() => registry.RequireActive(old));
        registry.RequireActive(current); registry.End(second);
    }
    [TestMethod] public async Task FailedSettingsWriteCannotPublishDraftOrChangeProxy()
    {
        var network = new NetworkOptions(); using var http = new HttpClient(new HttpClientHandler { UseProxy = false }); var downloader = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloader, network);
        await service.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json.tmp"));
        var old = service.Snapshot.Settings;
        await Assert.ThrowsAsync<Exception>(() => service.SaveSettingsAsync(old with { MemoryMiB = 1536, Proxy = new(ProxyMode.Direct) }));
        Assert.AreEqual(old, service.Snapshot.Settings); Assert.AreEqual(ProxyMode.System, network.Capture().Proxy.Mode);
    }
    [TestMethod] public void NetworkSettingsAreCapturedAsOneRevision()
    {
        var options = new NetworkOptions(); options.Update(new(Proxy: new(ProxyMode.Direct), Aria2: new(true)));
        var before = options.Capture(); options.Update(new(Proxy: new(ProxyMode.Custom, "http://127.0.0.1:1"), Aria2: new(false)));
        Assert.AreEqual(ProxyMode.Direct, before.Proxy.Mode); Assert.IsTrue(before.Aria2.Enabled);
        var after = options.Capture(); Assert.AreEqual(ProxyMode.Custom, after.Proxy.Mode); Assert.IsFalse(after.Aria2.Enabled); Assert.IsGreaterThan(before.Revision, after.Revision);
    }
}
