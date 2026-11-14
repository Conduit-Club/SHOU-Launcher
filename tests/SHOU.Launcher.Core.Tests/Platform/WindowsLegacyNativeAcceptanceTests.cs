using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Core.Tests.Platform;

/// <summary>Opt-in local acceptance only. It never copies the source container or logs identities/secrets.</summary>
[TestClass]
public sealed class WindowsLegacyNativeAcceptanceTests
{
    [TestMethod, TestCategory("LocalLegacyAcceptance")]
    public async Task CurrentUserLegacyReadProtectedMigrationReloadAndMicrosoftRefresh()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows native acceptance only."); return; }
        if (Environment.GetEnvironmentVariable("SHOU_R50_LEGACY_NATIVE") != "1")
            Assert.Inconclusive("Explicit current-user legacy acceptance was not enabled.");
        var executable = Environment.GetEnvironmentVariable("SHOU_R50_LEGACY_DIRECTORY") ?? throw new InvalidOperationException("Explicit project legacy directory is required.");
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLCE");
        var config = Path.Combine(shared, "config.v1.json"); var keyFile = Path.Combine(shared, "UserKey.bin");
        var before = new[] { config, keyFile }.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = timeout.Token;
        var data = await new WindowsLegacyDesktopDataReader(executable, shared).ReadAsync(ct);
        var fixture = new PlatformFixture();
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };
        var platform = PlatformServicesFactory.Create(new(fixture.Child("native private migration")), new VerifiedDownloader(http))
            with { LegacyDesktopData = new Source(data) };
        using var lease = platform.DataLock.Acquire(platform.Paths);
        var migrated = 0; var refreshed = 0; string? failure = null;
        try
        {
            await using (var service = new LauncherService(platform, http, new VerifiedDownloader(http), new()))
            {
                await service.InitializeAsync(ct); migrated = service.Snapshot.Accounts.Count;
                foreach (var account in service.Snapshot.Accounts.Where(a => a.Persisted && a.Kind != AccountKind.Offline))
                {
                    var credentialKey = "account:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Id)));
                    var bytes = await new WindowsDpapiCredentialStore(platform.Paths).ReadAsync(credentialKey, ct);
                    Assert.IsNotNull(bytes);
                    try { Assert.IsTrue(AccountIdentity.Matches(account, JsonSerializer.Deserialize<AuthSession>(bytes, ConfigurationStore.JsonOptions)!.Profile)); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                // The public OAuth client ID is supplied in memory by the local acceptance runner.
                if (Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID") is { Length: > 0 } clientId)
                {
                    await service.SaveSettingsAsync(service.Snapshot.Settings with { MicrosoftClientId = clientId }, ct);
                    foreach (var account in service.Snapshot.Accounts.Where(a => a.Kind == AccountKind.Microsoft && a.Persisted).Take(1))
                        try { await service.RefreshAccountAsync(account.Id, ct); refreshed++; }
                        catch (AuthenticationException ex) { failure = ex.Code; }
                        catch (HttpRequestException) { failure = "network_http"; }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "network_timeout"; }
                }
            }
            await using var reopened = new LauncherService(platform, http, new VerifiedDownloader(http), new());
            await reopened.InitializeAsync(ct); Assert.AreEqual(migrated, reopened.Snapshot.Accounts.Count);
            foreach (var pair in before) Assert.IsTrue(CryptographicOperations.FixedTimeEquals(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key))), "Original legacy file must remain byte-identical.");
            var report = new { accountCount = migrated, recovered = reopened.Snapshot.Accounts.Count(a => a.Persisted), instanceCount = reopened.Snapshot.Instances.Count,
                microsoftRefreshed = refreshed, refreshFailureCategory = failure, issues = data.Issues, sourceFilesUnchanged = true, dpapiReloadVerified = true, gameStarted = false };
            await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "reports", "r50-legacy-native.json"), JsonSerializer.Serialize(report, ConfigurationStore.JsonOptions), ct);
            Assert.IsTrue(migrated > 0, "No compatible legacy account was recovered; see the redacted report.");
        }
        finally { await ((IAsyncDisposable)platform.Processes).DisposeAsync(); }
    }
    private sealed class Source(LegacyDesktopData data) : ILegacyDesktopDataReader
    { public Task<LegacyDesktopData> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(data); }
}
