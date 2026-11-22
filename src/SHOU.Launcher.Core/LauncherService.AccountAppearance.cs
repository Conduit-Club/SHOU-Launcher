using System.Collections.Concurrent;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IAccountAppearanceService, IAccountAppearanceMutationService
{
    private readonly ConcurrentDictionary<string, AccountAppearance> accountAppearances = new(StringComparer.Ordinal);
    private readonly Func<HttpClient>? accountAppearanceTransportFactory;
    private readonly ConcurrentDictionary<string, string> appearanceAuthenticatedTokens = new(StringComparer.Ordinal);
    public async Task<AccountAppearanceWriteResult> ChangeAccountAppearanceAsync(string accountId, AccountAppearanceChange change, CancellationToken cancellationToken = default)
    {
        if (change.Png.Length > AccountAppearancePng.MaximumPngBytes) throw new InvalidDataException("皮肤 PNG 超过 2 MiB 上限。");
        change = change with { Png = change.Png.ToArray() }; AccountAppearanceWriteResult? result = null; string? backup = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var account = FindAccount(accountId);
            if (Snapshot.SelectedAccountId != accountId) throw new InvalidOperationException("当前角色已改变，请重新选择外观。");
            if (account.Kind != AccountKind.Microsoft || !sessions.TryGetValue(accountId, out var session) || !AccountIdentity.Matches(account, session.Profile))
                throw new InvalidOperationException("请先登录此 Microsoft 角色。");
            using var client = CreateAccountAppearanceClient();
            result = await new AccountAppearanceClient(client).ChangeMicrosoftAsync(account, session.AccessToken, change, async (current, token) =>
            {
                if (Snapshot.SelectedAccountId != accountId || !AccountIdentity.Matches(FindAccount(accountId), account)) throw new InvalidOperationException("当前角色已改变，未发送在线修改。");
                backup = await new AccountAppearanceStore(Paths.LocalDataDirectory).BackupOnlineAsync(account, current, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            accountAppearances.TryRemove(accountId, out _);
            if (result.Appearance is { } verified && AccountIdentity.Matches(FindAccount(accountId), account)) accountAppearances[accountId] = verified;
        }, cancellationToken).ConfigureAwait(false);
        return result! with { BackupDirectory = backup is null ? null : Path.GetDirectoryName(backup) };
    }
    public async Task<AccountAppearance> GetAccountAppearanceAsync(string accountId, bool refreshRemote = false, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false); var account = FindAccount(accountId);
        if (account.Kind == AccountKind.Offline) return await new AccountAppearanceStore(Paths.LocalDataDirectory).LoadOfflineAsync(account, cancellationToken).ConfigureAwait(false);
        if (!refreshRemote) return accountAppearances.TryGetValue(accountId, out var cached) ? cached : AccountAppearanceClient.Unread(account);
        using var client = CreateAccountAppearanceClient();
        var reader = new AccountAppearanceClient(client); AccountAppearance result;
        if (account.Kind == AccountKind.Microsoft)
        {
            var session = await EnsureAppearanceSessionAsync(accountId, null, cancellationToken).ConfigureAwait(false);
            try { result = await reader.ReadMicrosoftAsync(account, session.AccessToken, cancellationToken).ConfigureAwait(false); }
            catch (AuthenticationException ex) when (ex.Code == "appearance_unauthorized")
            {
                // Retry only this read after one serialized refresh, never replay an appearance mutation.
                session = await EnsureAppearanceSessionAsync(accountId, session.AccessToken, cancellationToken).ConfigureAwait(false);
                result = await reader.ReadMicrosoftAsync(FindAccount(accountId), session.AccessToken, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _ = await EnsureAppearanceSessionAsync(accountId, null, cancellationToken).ConfigureAwait(false);
            result = await reader.ReadYggdrasilAsync(FindAccount(accountId), cancellationToken).ConfigureAwait(false);
        }
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
    private HttpClient CreateAccountAppearanceClient() => accountAppearanceTransportFactory?.Invoke()
        ?? new(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));

    private static string AppearanceTokenHash(string accessToken)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accessToken)));

    private async Task<AuthSession> EnsureAppearanceSessionAsync(string accountId, string? rejectedToken, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await operation.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var account = FindAccount(accountId);
            if (!sessions.TryGetValue(accountId, out var current) || !AccountIdentity.Matches(account, current.Profile))
                throw new AuthenticationException("missing_credentials", "无法恢复此账户的旧凭据，请重新登录；原记录保留。");
            var refresh = rejectedToken is not null ? current.AccessToken == rejectedToken
                : account.Kind == AccountKind.Microsoft
                    ? !string.IsNullOrEmpty(current.RefreshToken) && (current.Profile.ExpiresAt is null || current.Profile.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(2))
                    : !appearanceAuthenticatedTokens.TryGetValue(accountId, out var authenticated) || authenticated != AppearanceTokenHash(current.AccessToken);
            if (refresh)
            {
                var previousState = Snapshot.State;
                Set(s => s with { State = "Authenticating" });
                try { current = await RefreshInternalAsync(account, linked.Token, selectAccount: false).ConfigureAwait(false); }
                finally { Set(s => s.State == "Authenticating" ? s with { State = previousState } : s); }
            }
            return current;
        }
        finally { operation.Release(); }
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
