using System.Collections.Concurrent;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IAccountAppearanceService
{
    private readonly ConcurrentDictionary<string, AccountAppearance> accountAppearances = new(StringComparer.Ordinal);
    public async Task<AccountAppearance> GetAccountAppearanceAsync(string accountId, bool refreshRemote = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var account = FindAccount(accountId);
        if (account.Kind == AccountKind.Offline) return await new AccountAppearanceStore(Paths.LocalDataDirectory).LoadOfflineAsync(account, cancellationToken).ConfigureAwait(false);
        if (!refreshRemote) return accountAppearances.TryGetValue(accountId, out var cached) ? cached : AccountAppearanceClient.Unread(account);
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        var reader = new AccountAppearanceClient(client); AccountAppearance result;
        if (account.Kind == AccountKind.Microsoft)
        {
            if (!sessions.TryGetValue(accountId, out var session) || !AccountIdentity.Matches(account, session.Profile)) throw new InvalidOperationException("请先登录此 Microsoft 角色，再刷新外观。");
            result = await reader.ReadMicrosoftAsync(account, session.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        else result = await reader.ReadYggdrasilAsync(account, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AccountIdentity.Matches(FindAccount(accountId), account)) throw new InvalidOperationException("读取期间账户来源已改变，结果未应用。");
        accountAppearances[accountId] = result;
        while (accountAppearances.Count > 8)
        {
            var otherAccount = accountAppearances.Keys.FirstOrDefault(id => id != accountId);
            if (otherAccount is null) break;
            accountAppearances.TryRemove(otherAccount, out _);
        }
        return result;
    }
    public async Task<AccountAppearance> SaveOfflineAccountSkinAsync(string accountId, ReadOnlyMemory<byte> png, AccountSkinModel model, CancellationToken cancellationToken = default)
    {
        if (png.Length > AccountAppearancePng.MaximumPngBytes) throw new InvalidDataException("皮肤 PNG 超过 2 MiB 上限。");
        AccountAppearance? result = null; var bytes = png.ToArray();
        await ExecuteAsync("Ready", async ct =>
        {
            var account = FindAccount(accountId);
            if (Snapshot.SelectedAccountId != accountId) throw new InvalidOperationException("当前角色已改变，请为新选中的角色重新选择皮肤。");
            if (account.Kind != AccountKind.Offline) throw new InvalidOperationException("在线角色的皮肤请前往对应官网修改。");
            result = await new AccountAppearanceStore(Paths.LocalDataDirectory).SaveOfflineAsync(account, bytes, model, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
