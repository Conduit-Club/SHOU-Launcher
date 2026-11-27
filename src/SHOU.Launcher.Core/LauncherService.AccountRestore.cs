using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    // Runs during eager initialization or under the operation gate. Desktop reads
    // only the requested credential until an account-list write needs the full roster.
    private async Task RestoreAllAccountsAsync(CancellationToken ct)
    {
        if (allAccountsRestored) return;
        foreach (var account in Snapshot.Accounts.Where(a => a.Kind != AccountKind.Offline))
            await RestoreAccountAsync(account, ct).ConfigureAwait(false);
        allAccountsRestored = true;
    }

    private async Task RestoreAccountAsync(AccountProfile account, CancellationToken ct)
    {
        if (allAccountsRestored || sessions.ContainsKey(account.Id) || restoredAccounts.Contains(account.Id)) return;
        try
        {
            var bytes = await platform.Credentials.ReadAsync(AccountKey(account.Id), ct).ConfigureAwait(false);
            if (bytes is null) ReplaceAccount(account with { Persisted = false, Status = "凭据缺失 · 请重新登录" });
            else
            {
                try
                {
                    var session = JsonSerializer.Deserialize<AuthSession>(bytes, ConfigurationStore.JsonOptions)
                        ?? throw new InvalidDataException("Empty credential record.");
                    if (session.Profile is null || !AccountIdentity.Matches(account, session.Profile))
                        throw new InvalidDataException("Protected account identity/origin mismatch.");
                    var trusted = session.Profile with { Persisted = true, Status = "已保存 · 启动时验证" };
                    RegisterSession(session);
                    sessions[account.Id] = session with { Profile = trusted };
                    ReplaceAccount(trusted);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (Exception ex) when (ex is CredentialStoreException or JsonException or InvalidDataException or IOException)
        {
            Set(s => s with { CanPersistAccounts = false });
            ReplaceAccount(account with { Status = "凭据读取失败 · 原数据保留" });
            Log("Warning", "账户凭据无法读取，已禁止账户持久写入；原因类型：" + ex.GetType().Name);
        }
        restoredAccounts.Add(account.Id);
    }
}
