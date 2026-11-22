using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests.Platform;

/// <summary>Opt-in real profile GET using an isolated copy of the selected protected session; never starts Java.</summary>
[TestClass]
public sealed class WindowsAppearanceNativeAcceptanceTests
{
    [TestMethod, TestCategory("NativeCredential"), TestCategory("LocalAppearanceAcceptance")]
    public async Task CurrentSelectedProtectedSessionAutomaticallyLoadsItsRealSkinWithoutChangingOriginalFiles()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SHOU_LOCAL_APPEARANCE_ACCEPTANCE") != "1")
            Assert.Inconclusive("Explicit current-user Windows appearance acceptance is required.");
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCLCE", "shou.desktop.v1.json");
        var configBytes = await File.ReadAllBytesAsync(configPath);
        var data = JsonNode.Parse(configBytes)!;
        var selected = (string?)data["selectedAccountId"];
        var account = data["accounts"]!.Deserialize<AccountProfile[]>(ConfigurationStore.JsonOptions)!.Single(a => a.Id == selected);
        Assert.AreEqual(AccountKind.Microsoft, account.Kind, "This opt-in scenario requires the selected Microsoft account.");
        var key = "account:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account.Id)));
        var filename = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin";
        var protectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLCE", "credentials-v1", filename);
        var protectedBefore = SHA256.HashData(await File.ReadAllBytesAsync(protectedPath));
        var root = new PlatformFixture().Child("isolated native portrait");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var downloader = new VerifiedDownloader(http); var platform = PlatformServicesFactory.Create(new(root), downloader);
        using var lease = platform.DataLock.Acquire(platform.Paths);
        Directory.CreateDirectory(Path.Combine(platform.Paths.SharedDataDirectory, "credentials-v1"));
        File.Copy(protectedPath, Path.Combine(platform.Paths.SharedDataDirectory, "credentials-v1", filename));
        await using (var config = new ConfigurationStore(platform.Paths))
            await config.SaveAsync(new(new(Proxy: new(ProxyMode.System)), [], [account], account.Id));
        try
        {
            await using var service = new LauncherService(platform, http, downloader, new());
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await service.InitializeAsync(timeout.Token);
            Assert.IsTrue(service.Snapshot.Accounts.Single().Persisted, "Native protected session must be restored.");
            Assert.IsTrue(service.Snapshot.Accounts.Single().ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2), "Use a currently valid session for this read-only native acceptance; refresh behavior has isolated protocol coverage.");
            var appearance = await service.GetAccountAppearanceAsync(account.Id, true, timeout.Token);
            Assert.IsNotNull(appearance.Skin); Assert.IsTrue(appearance.IsRemote); Assert.AreEqual(account.Id, appearance.AccountId);
            Assert.AreEqual(account.Id, service.Snapshot.SelectedAccountId);
            CollectionAssert.AreEqual(configBytes, await File.ReadAllBytesAsync(configPath));
            CollectionAssert.AreEqual(protectedBefore, SHA256.HashData(await File.ReadAllBytesAsync(protectedPath)));
            var report = new { protectedSessionRestored = true, skinWidth = appearance.Skin.Width, skinHeight = appearance.Skin.Height,
                capes = appearance.Capes.Count, sourceFilesUnchanged = true, gameStarted = false, boundary = "native credential/API read; no pixel capture" };
            await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "reports", "native-real-portrait.json"), JsonSerializer.Serialize(report, ConfigurationStore.JsonOptions));
        }
        finally { await ((IAsyncDisposable)platform.Processes).DisposeAsync(); }
    }
}
