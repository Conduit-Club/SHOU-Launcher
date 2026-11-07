using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LegacyMicrosoftImportTests
{
    [AvaloniaFact]
    public void MicrosoftFormUsesFreshAuthorizationWithoutLegacyImportOrMinecraftLink()
    {
        var fake = new FakeLauncherService { SupportsLegacyMicrosoftImport = true,
            LegacyReadBehavior = _ => throw new InvalidOperationException("The login UI must not inspect old caches.") };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            vm.Accounts.SelectProviderCommand.Execute("microsoft"); Pump(window);
            var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
            Assert.DoesNotContain(controls, c => c.Name is "LegacyMicrosoftImportPanel" or "ReadLegacyMicrosoftAccountsButton" or "LegacyMicrosoftAccountSelector");
            Assert.DoesNotContain(controls.OfType<Button>(), b => b.Content as string == "前往 Minecraft 官网");
            var login = Assert.Single(controls.OfType<Button>(), b => b.Name == "LoginMicrosoftButton");
            Assert.True(login.IsEffectivelyVisible); Assert.Same(vm.Accounts.LoginMicrosoftCommand, login.Command);
            Assert.Null(fake.LastImportedLegacySource);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void SwitchingChannelsRefreshesAddingSummaryAndKeepsPasswordOnlySkinStationLogin()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new Window { Width = 300, Height = 600, Content = new AccountsView { DataContext = vm.Accounts } }; window.Show();
        try
        {
            var changes = new List<string?>(); vm.Accounts.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            vm.Accounts.SelectProviderCommand.Execute("microsoft"); vm.Accounts.Password = "transient-fixture";
            vm.Accounts.SelectProviderCommand.Execute("mua"); Pump(window);
            Assert.Contains("MUA", vm.Accounts.AddingAccountSummary); Assert.Equal("", vm.Accounts.Password);
            Assert.Contains(nameof(vm.Accounts.AddingAccountSummary), changes);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => (b.Content as string)?.Contains("OAuth") == true);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "YggdrasilPassword" && t.PasswordChar == '●');
            vm.Accounts.SelectProviderCommand.Execute("offline"); Assert.Contains("离线", vm.Accounts.AddingAccountSummary);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void OfflineExplanationAndChineseWarningAreContextual()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new Window { Width = 300, Height = 600, Content = new AccountsView { DataContext = vm.Accounts } }; window.Show();
        try
        {
            vm.Accounts.SelectProviderCommand.Execute("offline"); vm.Accounts.BeginAddAccountCommand.Execute(null); Pump(window);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OfflineChannelButton");
            Assert.Contains("不推荐中文", Assert.IsType<string>(ToolTip.GetTip(button)));
            var warning = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "OfflineNameWarning");
            vm.Accounts.PlayerName = null!; Assert.False(vm.Accounts.AddOfflineCommand.CanExecute(null));
            vm.Accounts.PlayerName = "Player_1"; Pump(window); Assert.False(warning.IsEffectivelyVisible);
            vm.Accounts.PlayerName = "中文角色"; Pump(window); Assert.True(warning.IsEffectivelyVisible); Assert.True(vm.Accounts.AddOfflineCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
