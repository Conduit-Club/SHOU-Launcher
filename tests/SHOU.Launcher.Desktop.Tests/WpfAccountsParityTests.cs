using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfAccountsParityTests
{
    private static readonly AccountProfile[] Profiles =
    [new("offline", AccountKind.Offline, "同名样例", new string('1', 32)),
     new("ms", AccountKind.Microsoft, "同名样例", new string('2', 32), Persisted: true),
     new("mua", AccountKind.Yggdrasil, "同名样例", new string('3', 32), "https://skin.mualliance.ltd/api/yggdrasil"),
     new("little", AccountKind.Yggdrasil, "同名样例", new string('4', 32), "https://littleskin.cn/api/yggdrasil"),
     new("other", AccountKind.Yggdrasil, "同名样例", new string('5', 32), "https://littleskin.cn.example.invalid/api/yggdrasil")];
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static FakeLauncherService ServiceWithProfiles()
    {
        var service = new FakeLauncherService(); service.Publish(service.Snapshot with { Accounts = Profiles, SelectedAccountId = "ms" }); return service;
    }

    [AvaloniaFact]
    public void AccountPanelFitsThreeHundredDipAndCountsEachRealProvider()
    {
        using var root = new MainWindowViewModel(ServiceWithProfiles(), new FakeExternalLauncher());
        var window = new Window { Width = 300, Height = 474, Content = new AccountsView { DataContext = root.Accounts } };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(260, Find<StackPanel>(window, "CompactAccountsPanel").Bounds.Width);
            Assert.Equal(260, Find<Grid>(window, "AccountChannelGrid").Bounds.Width);
            Assert.Contains("（1）", root.Accounts.MicrosoftChannelTitle); Assert.Contains("（1）", root.Accounts.LittleSkinChannelTitle);
            Assert.Contains("（1）", root.Accounts.MuaChannelTitle); Assert.Contains("（1）", root.Accounts.OfflineChannelTitle);
            Assert.Equal("microsoft", root.Accounts.SelectedProvider.Id); Assert.Single(root.Accounts.ChannelAccounts);
            Assert.Equal("ms", root.Accounts.SelectedChannelAccount!.Id);
            Assert.Equal(4, window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("accountChannel")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ProviderBrowsingKeepsActualSelectionAndClearsPasswords()
    {
        var service = ServiceWithProfiles();
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var accounts = root.Accounts;
        accounts.Password = "fixture-transient-password";
        accounts.SelectProviderCommand.Execute("littleskin");
        Assert.Equal("", accounts.Password); Assert.Equal("ms", service.Snapshot.SelectedAccountId);
        Assert.Single(accounts.ChannelAccounts); Assert.Equal("little", accounts.ChannelAccounts[0].Id);
        accounts.SelectedChannelAccount = accounts.ChannelAccounts[0];
        Assert.Equal("little", service.LastSelectedAccountId); Assert.Equal("little", service.Snapshot.SelectedAccountId);
        accounts.ToggleAllAccountsCommand.Execute(null);
        accounts.SelectedAccount = Profiles[2];
        Assert.Equal("mua", accounts.SelectedProvider.Id); Assert.Equal("mua", service.Snapshot.SelectedAccountId);
        Assert.True(accounts.ShowAllAccounts); Assert.Equal(5, accounts.Accounts.Count);
        accounts.Password = "fixture-cleared-on-leave";
        root.SelectedPage = root.Settings;
        Assert.Equal("", accounts.Password);
    }

    [AvaloniaFact]
    public async Task CompactOfflineFormUsesRealAddAndClosesAfterSuccessfulSave()
    {
        var service = new FakeLauncherService();
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var accounts = root.Accounts;
        accounts.BeginAddAccountCommand.Execute(null);
        Assert.True(accounts.IsAddingAccount); Assert.False(accounts.AddOfflineCommand.CanExecute(null));
        accounts.PlayerName = "ParityFixture";
        await accounts.AddOfflineCommand.ExecuteAsync(null);
        Assert.False(accounts.IsAddingAccount); Assert.Contains(service.Snapshot.Accounts, a => a.DisplayName == "ParityFixture");
        Assert.Equal(service.Snapshot.SelectedAccountId, accounts.SelectedChannelAccount!.Id);
    }

    [AvaloniaFact]
    public async Task CompactSkinStationFormCallsOnlySelectedEndpointAndDropsPassword()
    {
        var service = new FakeLauncherService();
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        root.Accounts.SelectProviderCommand.Execute("mua"); root.Accounts.BeginAddAccountCommand.Execute(null);
        root.Accounts.Username = "fixture-user"; root.Accounts.Password = "fixture-transient-password";
        await root.Accounts.LoginYggdrasilCommand.ExecuteAsync(null);
        Assert.Equal("https://skin.mualliance.ltd/api/yggdrasil", service.LastYggdrasilServer!.AbsoluteUri);
        Assert.Equal("fixture-transient-password", service.LastYggdrasilPassword);
        Assert.Equal("", root.Accounts.Password); Assert.False(root.Accounts.IsAddingAccount);
        Assert.DoesNotContain("fixture-transient-password", root.Logs.ExportText);
    }
}
