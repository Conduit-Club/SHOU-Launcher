using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class GameStartupPresentationTests
{
    [AvaloniaFact]
    public void JavaHandoffReturnsToNormalHomeWhileTheWindowObserverWaits()
    {
        var service = new FakeLauncherService();
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show(); Pump(window);
        try
        {
            PublishWaiting(service, 43210); Pump(window);
            Assert.False(vm.IsBusy); Assert.True(vm.CanMutate);
            Assert.False(Find<Grid>(window, "HomeLaunchProgressPanel").IsEffectivelyVisible);
            Assert.True(Find<Button>(window, "HomeLaunchButton").IsEffectivelyVisible);
            Assert.Equal("启动游戏", vm.Instances.LaunchCaption);
            Assert.DoesNotContain("完成", vm.StateSummary);
            service.Publish(service.Snapshot with { LaunchActivity = null, GameSessions = [service.Snapshot.GameSessions[0] with { WindowReady = true }] }); Pump(window);
            Assert.False(vm.IsHomeLaunching);
            Assert.False(Find<Grid>(window, "HomeLaunchProgressPanel").IsEffectivelyVisible);
            Assert.True(Find<AccountsView>(window, "HomeAccountsView").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GameWindowObservationLeavesGameAliveAndNormalLaunchAvailable()
    {
        var service = new FakeLauncherService();
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show(); Pump(window);
        try
        {
            PublishWaiting(service, 43210); Pump(window);
            Assert.False(vm.IsHomeLaunching);
            Assert.Equal(0, service.StopCalls); Assert.Equal(43210, service.Snapshot.GameProcessId);
            Assert.True(Find<Button>(window, "HomeLaunchButton").IsEffectivelyEnabled);
            Assert.True(vm.Instances.LaunchCommand.CanExecute(null));
            await vm.Instances.LaunchCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal("none", service.LastLaunchServerId); Assert.Equal(0, service.StopCalls);
            PublishWaiting(service, 43211); Pump(window);
            Assert.False(vm.IsHomeLaunching);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HidePolicyWaitsForTheMatchingVisibleWindowAndRestoresOnExit()
    {
        var service = new FakeLauncherService();
        service.Publish(service.Snapshot with { Settings = service.Snapshot.Settings with { LaunchOptions = new(Visibility: LauncherVisibility.Hide) } });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show(); Pump(window);
        try
        {
            PublishWaiting(service, 43210); Pump(window); Assert.True(window.IsVisible);
            service.Publish(service.Snapshot with { GameSessions = [service.Snapshot.GameSessions[0] with { WindowReady = true }], LaunchActivity = null }); Pump(window);
            Assert.False(window.IsVisible);
            PublishWaiting(service, 43211); Pump(window);
            Assert.True(window.IsVisible, "If the last visible game exits while another is still initializing, the launcher must return.");
            service.Publish(service.Snapshot with { GameSessions = [service.Snapshot.GameSessions[0] with { WindowReady = true }], LaunchActivity = null }); Pump(window);
            Assert.False(window.IsVisible);
            service.Publish(service.Snapshot with { GameSessions = [], GameProcessId = null }); Pump(window);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(43210, 43210, 0, 854, 480, 1, true)]
    [InlineData(43210, 43211, 0, 854, 480, 1, false)]
    [InlineData(43210, 43210, 0, 1, 1, 1, false)]
    [InlineData(43210, 43210, 0, 854, 480, 0, false)]
    [InlineData(43210, 43210, 1, 854, 480, 1, false)]
    [InlineData(43210, 43210, 0, 0, 480, 1, false)]
    [InlineData(0, 0, 0, 854, 480, 1, false)]
    [InlineData(43210, 43210, 0, double.NaN, 480, 1, false)]
    [InlineData(43210, 43210, 0, double.PositiveInfinity, 480, 1, false)]
    public void MacProbeRejectsUnrelatedAndInvisibleHelperWindows(int target, double owner, double layer, double width, double height, double alpha, bool expected)
        => Assert.Equal(expected, MacGameWindowQuery.IsGameWindow(target, owner, layer, width, height, alpha));

    private static void PublishWaiting(FakeLauncherService service, int pid)
    {
        var snapshot = service.Snapshot; var instance = snapshot.Instances[0]; var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        service.Publish(snapshot with { State = "Ready", GameProcessId = pid,
            GameSessions = [new(instance.Id, pid, started) { WindowReady = false }],
            LaunchActivity = new(instance.Id, instance.Name, "等待游戏窗口", "离线角色", ProcessId: pid, ProcessStartedAt: started) });
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
