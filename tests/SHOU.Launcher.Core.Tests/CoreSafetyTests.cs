using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CoreSafetyTests
{
    [TestMethod] public void BoundaryComparisonNeverCaseFoldsMacOrLinuxRoots()
    {
        var parent = TestFiles.Root(); var root = Path.Combine(parent, "OwnedHome");
        Assert.AreEqual(OperatingSystem.IsWindows(), SafePath.IsWithin(root, Path.Combine(parent, "ownedhome", "client.jar")),
            "macOS Path.GetRelativePath ignores case even on a case-sensitive volume; it must not be a boundary guard.");
        Assert.IsTrue(SafePath.IsWithin(root, root));
        Assert.IsTrue(SafePath.IsWithin(root, Path.Combine(root, "libraries/client.jar")));
        Assert.IsFalse(SafePath.IsWithin(root, Path.Combine(parent, "OwnedHomeSibling", "client.jar")));
        Assert.IsFalse(SafePath.IsWithin(root, parent));
    }
    [TestMethod] public async Task ConfigurationSymlinkCannotReadOrOverwriteAnotherRoot()
    {
        if (OperatingSystem.IsWindows()) return; // Native Windows reparse policy is a separate gate.
        var paths = new AppPaths(new(TestFiles.Root()), new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "26"));
        Directory.CreateDirectory(paths.LocalDataDirectory);
        var outside = Path.Combine(TestFiles.Root(), "preserve.json");
        await File.WriteAllTextAsync(outside, "must survive");
        File.CreateSymbolicLink(Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json"), outside);
        Assert.ThrowsExactly<IOException>(() => new ConfigurationStore(paths));
        Assert.AreEqual("must survive", await File.ReadAllTextAsync(outside));
    }
    [TestMethod] public async Task UnhashedCatalogIsAlwaysFreshAndTypedParseIsSeparate()
    {
        var count = 0;
        using var server = new LocalHttpFixture(context => { var bytes = Encoding.UTF8.GetBytes("{\"catalog\":" + Interlocked.Increment(ref count) + "}"); context.Response.OutputStream.Write(bytes); context.Response.Close(); });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var downloader = new VerifiedDownloader(http); var file = Path.Combine(TestFiles.Root(), "catalog.json");
        var request = new DownloadRequest([server.Uri("catalog")], file);
        await downloader.DownloadAsync(request); Assert.AreEqual(1, (int?)JsonNode.Parse(await File.ReadAllTextAsync(file))?["catalog"]);
        await downloader.DownloadAsync(request); Assert.AreEqual(2, (int?)JsonNode.Parse(await File.ReadAllTextAsync(file))?["catalog"]);
        Assert.IsFalse(await VerifiedDownloader.IsValidAsync(file, request));
    }
    [TestMethod] public async Task DownloadSidecarSymlinksCannotOverwriteOtherFiles()
    {
        if (OperatingSystem.IsWindows()) return; // Reparse creation requires a native Windows host; Unix path is exercised here.
        using var http = new HttpClient(); var root = TestFiles.Root(); var outside = Path.Combine(root, "preserve");
        await File.WriteAllTextAsync(outside, "must survive");
        foreach (var suffix in new[] { "", ".part", ".part.resume.json", ".download.lock" })
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N")); File.CreateSymbolicLink(path + suffix, outside);
            await Assert.ThrowsExactlyAsync<IOException>(() => new VerifiedDownloader(http).DownloadAsync(new([new Uri("http://127.0.0.1:1/file")], path, Sha1: new string('0', 40))));
            Assert.AreEqual("must survive", await File.ReadAllTextAsync(outside));
        }
    }
    [TestMethod] public void LegacyX64NativeIsNotMisclassifiedAsArm64()
    {
        var metadata = JsonNode.Parse("""{"libraries":[{"name":"org.lwjgl:lwjgl:2.9.4","natives":{"osx":"natives-osx"},"downloads":{"classifiers":{"natives-osx":{"url":"https://example.invalid/native.jar","path":"native.jar"}}}}]}""")!.AsObject();
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LibraryResolver.Resolve(metadata, new("osx", "26", RuntimeArchitecture.Arm64)));
    }
    [TestMethod] public async Task SettingsPreserveUnknownKeysAndAccountReadFailureSnapshot()
    {
        var paths = new AppPaths(new(TestFiles.Root()), new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "26"));
        Directory.CreateDirectory(paths.LocalDataDirectory); var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
        await File.WriteAllTextAsync(file, """{"schemaVersion":1,"unknownTop":{"keep":42},"settings":{"memoryMiB":2048,"unknownSetting":"keep"},"accounts":[{"id":"existing","kind":0,"displayName":"LocalUser","playerUuid":"00000000000000000000000000000000"}]}""");
        await using var store = new ConfigurationStore(paths); var state = await store.LoadAsync();
        await store.SaveAsync(state with { Settings = state.Settings with { MemoryMiB = 1536 }, Accounts = [] }, writeAccounts: false);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.AreEqual(42, (int?)json["unknownTop"]?["keep"]); Assert.AreEqual("keep", (string?)json["settings"]?["unknownSetting"]);
        Assert.AreEqual(1, json["accounts"]!.AsArray().Count);
    }
    [TestMethod] public async Task FutureSchemaAndCorruptionCannotBeWrittenOver()
    {
        var paths = new AppPaths(new(TestFiles.Root()), new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "26"));
        Directory.CreateDirectory(paths.LocalDataDirectory); var file = Path.Combine(paths.LocalDataDirectory, "shou.desktop.v1.json");
        await File.WriteAllTextAsync(file, "{\"schemaVersion\":999,\"important\":true}"); var original = await File.ReadAllTextAsync(file);
        await using var store = new ConfigurationStore(paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(new(new(), [], [])));
        Assert.AreEqual(original, await File.ReadAllTextAsync(file));
    }
    [TestMethod] public async Task MicrosoftRefreshUsesFullProtocolAndExactXboxPropertyCase()
    {
        var bodies = new List<JsonObject>();
        using var handler = new ScriptedHttpHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/xbl" or "/xsts") bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject());
            return path switch
            {
                "/token" => ScriptedHttpHandler.Json("""{"access_token":"fake-oauth","refresh_token":"fake-refresh"}"""),
                "/xbl" => ScriptedHttpHandler.Json("""{"Token":"fake-xbl"}"""),
                "/xsts" => ScriptedHttpHandler.Json("""{"Token":"fake-xsts","DisplayClaims":{"xui":[{"uhs":"fake-hash"}]}}"""),
                "/mc" => ScriptedHttpHandler.Json("""{"access_token":"fake-minecraft","expires_in":3600}"""),
                "/entitlements" => ScriptedHttpHandler.Json("""{"items":[{"name":"game_minecraft"}]}"""),
                "/profile" => ScriptedHttpHandler.Json("""{"id":"11111111222233334444555555555555","name":"FixtureUser"}"""),
                _ => throw new InvalidOperationException("Unexpected endpoint")
            };
        });
        using var http = new HttpClient(handler); var client = new AuthenticationClient(http, Endpoints());
        var account = new AccountProfile("id", AccountKind.Microsoft, "FixtureUser", "11111111222233334444555555555555");
        var result = await client.RefreshMicrosoftAsync("11111111-2222-3333-4444-555555555555", new(account, "old", "fake-refresh"));
        Assert.AreEqual("fake-minecraft", result.AccessToken); Assert.AreEqual(account.Id, result.Profile.Id);
        Assert.IsNotNull(bodies[0]["Properties"]?["RpsTicket"]); Assert.IsNull(bodies[0]["properties"]);
    }
    [TestMethod] public async Task AuthenticationErrorsDoNotEchoBodiesAndCancellationStopsPolling()
    {
        using var handler = new ScriptedHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent("<html>fake-secret-password</html>") }));
        using var http = new HttpClient(handler); var client = new AuthenticationClient(http, Endpoints());
        var ex = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => client.LoginYggdrasilAsync(new Uri("http://127.0.0.1/ygg"), "fixture", "fake-secret-password"));
        Assert.IsFalse(ex.Message.Contains("fake-secret-password", StringComparison.Ordinal));
        using var deviceHandler = new ScriptedHttpHandler(_ => Task.FromResult(ScriptedHttpHandler.Json("""{"device_code":"fake-device","user_code":"FAKE-CODE","verification_uri":"http://127.0.0.1/verify","expires_in":60,"interval":1}""")));
        using var deviceHttp = new HttpClient(deviceHandler); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new AuthenticationClient(deviceHttp, Endpoints()).LoginMicrosoftAsync("fixture-client", new Progress<DeviceCodeChallenge>(), cancel.Token));
    }
    private static MicrosoftEndpoints Endpoints() => new(new("http://127.0.0.1/device"), new("http://127.0.0.1/token"), new("http://127.0.0.1/xbl"), new("http://127.0.0.1/xsts"), new("http://127.0.0.1/mc"), new("http://127.0.0.1/entitlements"), new("http://127.0.0.1/profile"));
}

internal sealed class ScriptedHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return respond(request); }
    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
