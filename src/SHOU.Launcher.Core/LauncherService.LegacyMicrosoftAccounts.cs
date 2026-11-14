using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    public bool SupportsLegacyMicrosoftImport => platform.LegacyMicrosoftAccounts is not null;

    public async Task<IReadOnlyList<LegacyMicrosoftAccountChoice>> ReadLegacyMicrosoftAccountsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LegacyMicrosoftAccountChoice> result = [];
        await ExecuteAsync("Ready", async ct =>
        {
            var source = platform.LegacyMicrosoftAccounts ?? throw new PlatformNotSupportedException("此平台没有旧 Windows 账户读取入口。");
            var credentials = await source.ReadAsync(ct).ConfigureAwait(false);
            result = credentials.Select(item => new LegacyMicrosoftAccountChoice(item.SourceId, item.DisplayName)).ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<AccountProfile> ImportLegacyMicrosoftAccountAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        AccountProfile? result = null;
        await ExecuteAsync("Authenticating", async ct =>
        {
            var source = platform.LegacyMicrosoftAccounts ?? throw new PlatformNotSupportedException("此平台没有旧 Windows 账户读取入口。");
            // Reread the original, never import a token retained by a UI or a screenshot fixture.
            var matches = (await source.ReadAsync(ct).ConfigureAwait(false)).Where(item => item.SourceId == sourceId).Take(2).ToArray();
            if (matches.Length != 1) throw new AuthenticationException("legacy_source_changed", "旧账户已变化，请重新读取并选择。");
            var credential = matches[0];
            redactor.Register(credential.RefreshToken);
            redactor.Register(credential.DisplayName);
            var original = new AuthSession(new("microsoft:" + credential.PlayerUuid, AccountKind.Microsoft,
                credential.DisplayName, credential.PlayerUuid), "", credential.RefreshToken);
            // The normal OAuth refresh -> Xbox -> Minecraft entitlement/profile path verifies
            // ownership and exact identity before the existing protected persistence path runs.
            var session = await authentication.RefreshMicrosoftAsync(ResolveClientId(), original, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            result = await SaveSessionAsync(session, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
