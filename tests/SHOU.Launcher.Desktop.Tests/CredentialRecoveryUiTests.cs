using Avalonia.Headless.XUnit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CredentialRecoveryUiTests
{
    [AvaloniaFact]
    public async Task ExistingAccountRecoveryIsExplicitAndDisappearsAfterAuthorization()
    {
        var service = new Service(); service.Publish(service.Snapshot with { CanPersistAccounts = false,
            Accounts = [new("fixture", AccountKind.Microsoft, "Fixture", "11111111222233334444555555555555", Persisted: true)], SelectedAccountId = "fixture" });
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        Assert.True(owner.Accounts.NeedsCredentialAuthorization); Assert.Equal(0, service.Calls);
        await owner.Accounts.RecoverCredentialAccessCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Calls); Assert.False(owner.Accounts.NeedsCredentialAuthorization);
    }
    private sealed class Service : FakeLauncherService, ICredentialAccessRecoveryService
    {
        public int Calls;
        public bool CanAuthorizeCredentialAccess => true;
        public Task RecoverCredentialAccessAsync(CancellationToken cancellationToken = default)
        { Calls++; Publish(Snapshot with { CanPersistAccounts = true }); return Task.CompletedTask; }
    }
}
