using Avalonia.Headless.XUnit;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfShellNavigationTests
{
    [AvaloniaFact]
    public void PrimaryRoutesAndContextBackPreserveExistingPageInstances()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        Assert.Same(vm, vm.Home.Root);
        Assert.Equal("launch", vm.CurrentSection);
        Assert.False(vm.IsContextPage);
        vm.ShowDownloadsCommand.Execute(null);
        Assert.Same(vm.Downloads, vm.SelectedPage);
        Assert.Equal("download", vm.CurrentSection);
        vm.ShowSettingsCommand.Execute(null);
        Assert.Equal("settings", vm.CurrentSection);
        vm.ShowLogsCommand.Execute(null);
        Assert.True(vm.IsContextPage);
        Assert.Equal(vm.Logs.Title, vm.NavigationTitle);
        vm.BackCommand.Execute(null);
        Assert.Same(vm.Settings, vm.SelectedPage);
        vm.ShowInstancesCommand.Execute(null);
        Assert.Equal("launch", vm.CurrentSection);
        vm.Instances.ShowInstanceSettingsCommand.Execute(null);
        Assert.False(vm.Instances.IsSelection);
        Assert.Equal(vm.Instances.ContextTitle, vm.NavigationTitle);
        vm.BackCommand.Execute(null);
        Assert.Same(vm.Instances, vm.SelectedPage);
        Assert.True(vm.Instances.IsSelection);
        Assert.Equal("实例选择", vm.NavigationTitle);
        vm.BackCommand.Execute(null);
        Assert.Same(vm.Home, vm.SelectedPage);
        vm.ShowAccountsCommand.Execute(null);
        Assert.Same(vm.Accounts, vm.SelectedPage);
        vm.ShowToolsCommand.Execute(null);
        Assert.Equal("tools", vm.CurrentSection);
        Assert.Contains(vm.Tools, vm.Pages);
        Assert.False(vm.BackCommand.CanExecute(null));
        vm.Tools.ShowToolboxCommand.Execute(null);
        Assert.True(vm.Tools.IsToolbox);
        vm.Tools.ShowLobbyCommand.Execute(null);
        Assert.True(vm.Tools.IsLobby);
    }
}
