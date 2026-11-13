using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Core.Tests;

[TestClass]
public sealed class CredentialAccessRecoveryTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("denied")]
    [DataRow("mismatch")]
    [DataRow("missing")]
    [DataRow("cancel")]
    public async Task ForegroundRecoveryReusesProtectedSessionWithoutLoginOrChangingOriginalFiles(string mode)
    {
        using var http = new HttpClient(new ScriptedHttpHandler(_ => throw new AssertFailedException("Recovering storage access must not log in or rotate tokens.")));
        var downloader = new VerifiedDownloader(http); var original = PlatformServicesFactory.Create(new(TestFiles.Root()), downloader);
        var account = new AccountProfile("existing", AccountKind.Microsoft, "Fixture", "11111111222233334444555555555555", Persisted: true);
        var session = new AuthSession(mode == "mismatch" ? account with { PlayerUuid = "aaaaaaaa222233334444555555555555" } : account, "synthetic-access", "synthetic-refresh");
        using var cancellation = new CancellationTokenSource();
        var store = new RecoveryStore(mode, session, cancellation.Cancel); var platform = original with { Credentials = store };
        await using (var config = new ConfigurationStore(platform.Paths)) await config.SaveAsync(new(new(), [], [account], account.Id));
        var path = Path.Combine(platform.Paths.LocalDataDirectory, "shou.desktop.v1.json"); var before = await File.ReadAllBytesAsync(path);
        await using var service = new LauncherService(platform, http, downloader, new()); await service.InitializeAsync();
        Assert.IsFalse(service.Snapshot.CanPersistAccounts); Assert.AreEqual(0, store.Authorizations);
        if (mode == "success")
        {
            await service.RecoverCredentialAccessAsync(); Assert.IsTrue(service.Snapshot.CanPersistAccounts);
            Assert.AreEqual("已保存 · 启动时验证", service.Snapshot.Accounts.Single().Status);
        }
        else if (mode == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.RecoverCredentialAccessAsync(cancellation.Token));
            Assert.IsFalse(service.Snapshot.CanPersistAccounts);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverCredentialAccessAsync());
            Assert.IsFalse(service.Snapshot.CanPersistAccounts);
        }
        Assert.AreEqual(1, store.Authorizations); Assert.AreEqual(0, store.Writes); Assert.AreEqual(0, store.Deletes);
        if (store.Returned is { } returned) Assert.IsTrue(returned.All(b => b == 0), "Temporary secret bytes must be cleared.");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        await ((ProcessRunner)original.Processes).DisposeAsync();
    }
    private sealed class RecoveryStore(string mode, AuthSession session, Action cancel) : ICredentialStore, IUserAuthorizedCredentialStore
    {
        public int Authorizations, Writes, Deletes;
        public byte[]? Returned;
        public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default) => throw new CredentialStoreException(CredentialFailure.Locked, "Synthetic authorization required.");
        public Task<byte[]?> ReadWithUserAuthorizationAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Authorizations++;
            if (mode == "denied") throw new CredentialStoreException(CredentialFailure.Denied, "Synthetic user denial.");
            Returned = mode == "missing" ? null : JsonSerializer.SerializeToUtf8Bytes(session, ConfigurationStore.JsonOptions);
            if (mode == "cancel") cancel();
            return Task.FromResult(Returned);
        }
        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Deletes++; return Task.CompletedTask; }
    }
}
