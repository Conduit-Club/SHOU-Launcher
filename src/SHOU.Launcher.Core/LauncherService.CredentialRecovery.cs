using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ICredentialAccessRecoveryService
{
    public bool CanAuthorizeCredentialAccess => platform.Credentials is IUserAuthorizedCredentialStore;
    public Task RecoverCredentialAccessAsync(CancellationToken cancellationToken = default) => ExecuteAsync("Authenticating", async ct =>
    {
        if (platform.Credentials is not IUserAuthorizedCredentialStore store) throw new PlatformNotSupportedException("当前安全存储不需要交互授权。");
        var incomplete = false;
        foreach (var account in Snapshot.Accounts.Where(a => a.Kind != AccountKind.Offline).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (account.Persisted && sessions.TryGetValue(account.Id, out var existing) && existing.Profile.Persisted) continue;
            try
            {
                var bytes = await store.ReadWithUserAuthorizationAsync(AccountKey(account.Id), ct).ConfigureAwait(false);
                if (bytes is null) { incomplete = true; ReplaceAccount(account with { Status = "未找到原登录凭据 · 原档案保留" }); continue; }
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var session = JsonSerializer.Deserialize<AuthSession>(bytes, ConfigurationStore.JsonOptions) ?? throw new InvalidDataException("原登录记录无法读取。");
                    if (session.Profile is null || !AccountIdentity.Matches(account, session.Profile)) throw new InvalidDataException("原登录记录的账户身份不符。");
                    var trusted = session.Profile with { Persisted = true, Status = "已保存 · 启动时验证" };
                    RegisterSession(session); sessions[account.Id] = session with { Profile = trusted }; ReplaceAccount(trusted);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is CredentialStoreException or JsonException or InvalidDataException or IOException)
            { incomplete = true; ReplaceAccount(account with { Status = "系统授权未完成或凭据不可读 · 原记录保留" }); Log("Warning", "重试安全存储未完成：" + ex.GetType().Name); }
        }
        ct.ThrowIfCancellationRequested();
        Set(s => s with { CanPersistAccounts = !incomplete });
        if (incomplete) throw new InvalidOperationException("部分原登录项尚未恢复。请完成 macOS 的钥匙串授权后重试；原凭据和账户列表均保留。");
        // Do not rotate tokens, rewrite old records or contact authentication servers just to recover access.
    }, cancellationToken);
}
