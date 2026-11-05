using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CandidateCapabilityTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ServerDestinationKeepsLaunchDisabledForEveryAccountKind()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show(); Pump(window);
        try
        {
            foreach (var kind in Enum.GetValues<AccountKind>())
            {
                var account = new AccountProfile("capability-fixture", kind, "Fixture", "00000000000000000000000000000001");
                fake.Publish(fake.Snapshot with { Accounts = [account], SelectedAccountId = account.Id });
                vm.SelectedPage = vm.Home; Pump(window);
                var selector = Find<ListBox>(window, "ServerSelector");
                selector.SelectedItem = vm.Home.Servers.Single(s => s.Id == "smp"); Pump(window);
                Assert.Contains("自动入服暂不可用", vm.Home.DestinationSummary);
                Assert.False(vm.Instances.LaunchCommand.CanExecute(null));
                vm.Instances.ShowInstanceSettingsCommand.Execute(null); Pump(window);
                Assert.False(Find<Button>(window, "LaunchGameButton").IsEffectivelyEnabled);
                Assert.Contains("自动入服暂不可用", vm.Instances.LaunchPrerequisite);
                Assert.Null(fake.LastLaunchServerId);
                vm.Home.SelectedServer = vm.Home.Servers.Single(s => s.Id == "none"); Pump(window);
                Assert.True(vm.Instances.LaunchCommand.CanExecute(null));
                Assert.True(Find<Button>(window, "LaunchGameButton").IsEffectivelyEnabled);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RemoteAria2DraftCannotWriteSecretsOrSettingsAndLocalRpcCanSave()
    {
        var fake = new FakeLauncherService();
        const string existingRemote = "https://rpc.example.invalid/jsonrpc";
        fake.Publish(fake.Snapshot with { Settings = fake.Snapshot.Settings with { Aria2 = new(true, existingRemote) } });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        vm.SelectedPage = vm.Settings; Pump(window);
        try
        {
            Assert.Equal(existingRemote, vm.Settings.Aria2Endpoint);
            vm.Settings.Theme = "Dark"; vm.Settings.Aria2Secret = "fixture-rpc-secret";
            Find<Button>(window, "SaveSettingsButton").Command!.Execute(null);
            await vm.Settings.SaveCommand.ExecutionTask!; Pump(window);
            Assert.True(vm.HasError); Assert.Contains("尚不支持远程 RPC", vm.ErrorMessage);
            Assert.Equal(0, fake.SaveCalls); Assert.Empty(fake.SavedSecretKinds);
            Assert.Equal(existingRemote, fake.Snapshot.Settings.Aria2!.Endpoint);
            Assert.Equal("System", fake.Snapshot.Settings.Theme);
            Assert.Contains("回退内部下载器", Find<TextBlock>(window, "Aria2CapabilityHint").Text);
            Find<TextBox>(window, "Aria2EndpointSetting").Text = "http://[::1]:6800/jsonrpc"; Pump(window);
            await vm.Settings.SaveCommand.ExecuteAsync(null); Pump(window);
            Assert.False(vm.HasError); Assert.Equal(1, fake.SaveCalls);
            Assert.Collection(fake.SavedSecretKinds, kind => Assert.Equal("aria2", kind));
            Assert.Equal("http://[::1]:6800/jsonrpc", fake.Snapshot.Settings.Aria2.Endpoint);
            Assert.Equal("Dark", fake.Snapshot.Settings.Theme); Assert.Equal("", vm.Settings.Aria2Secret);
        }
        finally { window.Close(); }
    }
}
