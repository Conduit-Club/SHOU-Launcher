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
    public async Task ServerDestinationIsPassedToSharedLaunchForEveryAccountKind()
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
                Assert.Contains("启动后前往", vm.Home.DestinationSummary);
                Assert.True(vm.Instances.LaunchCommand.CanExecute(null));
                vm.Instances.ShowInstanceSettingsCommand.Execute(null); Pump(window);
                Assert.True(Find<Button>(window, "LaunchGameButton").IsEffectivelyEnabled);
                await vm.Instances.LaunchCommand.ExecuteAsync(null); Pump(window);
                Assert.Equal("smp", fake.LastLaunchServerId);
                fake.Publish(fake.Snapshot with { State = "Ready", GameProcessId = null }); Pump(window);
                vm.Home.SelectedServer = vm.Home.Servers.Single(s => s.Id == "none"); Pump(window);
                Assert.True(vm.Instances.LaunchCommand.CanExecute(null));
                Assert.True(Find<Button>(window, "HomeLaunchButton").IsEffectivelyEnabled);
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
            Assert.Contains("回退内部下载器", Assert.IsType<string>(ToolTip.GetTip(Find<TextBlock>(window, "Aria2SectionTitle"))));
            Find<TextBox>(window, "Aria2EndpointSetting").Text = "http://[::1]:6800/jsonrpc"; Pump(window);
            await vm.Settings.SaveCommand.ExecuteAsync(null); Pump(window);
            Assert.False(vm.HasError); Assert.Equal(1, fake.SaveCalls);
            Assert.Collection(fake.SavedSecretKinds, kind => Assert.Equal("aria2", kind));
            Assert.Equal("http://[::1]:6800/jsonrpc", fake.Snapshot.Settings.Aria2.Endpoint);
            Assert.Equal("Dark", fake.Snapshot.Settings.Theme); Assert.Equal("", vm.Settings.Aria2Secret);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task Aria2ConnectionTestUsesSavedPortAndReportsStatusWithoutProcessControls()
    {
        var fake = new RpcService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        vm.SelectedPage = vm.Settings; vm.Settings.SelectSectionCommand.Execute("manage"); Pump(window);
        try
        {
            Assert.Equal("http://localhost:6800/jsonrpc", vm.Settings.Aria2Endpoint);
            var scroller = Find<ScrollViewer>(window, "SettingsPageScroller");
            scroller.Offset = new(0, scroller.Extent.Height); Pump(window);
            var offset = scroller.Offset; var firstSource = vm.Settings.Sources[0];
            await vm.Settings.TestAria2ConnectionCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal(1, fake.Probes);
            fake.Publish(fake.Snapshot with { }); Pump(window);
            Assert.Same(firstSource, vm.Settings.Sources[0]); Assert.Equal(offset, scroller.Offset);
            Assert.Contains("1.37.0", Find<TextBlock>(window, "Aria2ConnectionStatus").Text);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name is "StartAria2Button" or "StopAria2Button");
            vm.Settings.Aria2Endpoint = "http://localhost:6801/jsonrpc"; Pump(window);
            Assert.False(vm.Settings.TestAria2ConnectionCommand.CanExecute(null));
            await vm.Settings.SaveCommand.ExecuteAsync(null); Pump(window);
            await vm.Settings.TestAria2ConnectionCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal(2, fake.Probes); Assert.Equal("http://localhost:6801/jsonrpc", fake.Snapshot.Settings.Aria2!.Endpoint);
        }
        finally { window.Close(); }
    }
    private sealed class RpcService : FakeLauncherService, IAria2ConnectionService
    {
        public int Probes;
        public Task<Aria2ConnectionStatus> TestAria2ConnectionAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Probes++; return Task.FromResult(new Aria2ConnectionStatus(true, "已连接", Snapshot.Settings.Aria2?.Endpoint ?? "http://localhost:6800/jsonrpc", "1.37.0")); }
    }

}
