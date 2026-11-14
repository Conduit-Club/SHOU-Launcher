using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class AccountsPageViewModel
{
    public bool NeedsCredentialAuthorization => !Owner.Snapshot.CanPersistAccounts && Owner.Snapshot.Accounts.Any(a => a.Kind != AccountKind.Offline)
        && Service is ICredentialAccessRecoveryService { CanAuthorizeCredentialAccess: true };
    public AsyncRelayCommand RecoverCredentialAccessCommand { get; private set; } = null!;
    private void InitializeCredentialRecovery()
    {
        RecoverCredentialAccessCommand = AsyncCommand(async () =>
            await Owner.RunAsync("读取原登录凭据", ct => ((ICredentialAccessRecoveryService)Service).RecoverCredentialAccessAsync(ct)),
            () => CanEdit && NeedsCredentialAuthorization);
    }
}
