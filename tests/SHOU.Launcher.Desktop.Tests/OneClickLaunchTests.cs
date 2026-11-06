using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class OneClickLaunchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task HomeLaunchPreparesAnUninstalledFabricPackBeforeLaunchingTheCapturedDestination()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installs = 0;
        using var fixture = new Fixture(false, async ct => { installs++; entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        Task? running = null;
        try
        {
            Assert.False(fixture.Vm.Instances.SelectedInstance!.Installed);
            Assert.True(fixture.Launch.IsEffectivelyEnabled);
            Assert.Contains("首次", fixture.Vm.Instances.LaunchPrerequisite);
            running = fixture.ClickLaunch();
            await entered.Task.WaitAsync(Deadline); Pump(fixture.Window);
            Assert.Equal(1, installs); Assert.True(fixture.Vm.IsBusy);
            Assert.True(Find<Grid>(fixture.Window, "HomeLaunchProgressPanel").IsEffectivelyVisible);
            Assert.False(Find<AccountsView>(fixture.Window, "HomeAccountsView").IsEffectivelyVisible);
            Assert.False(Find<Border>(fixture.Window, "OperationBar").IsVisible);
            Assert.False(fixture.Launch.IsEffectivelyEnabled);
            Assert.Null(fixture.Service.LastLaunchServerId); Assert.Null(fixture.Service.Snapshot.GameProcessId);
            Assert.False(fixture.Service.Snapshot.Instances.Single().Installed);
            // A later presentation change cannot change the already requested launch destination.
            fixture.Vm.Home.SelectedServer = fixture.Vm.Home.Servers.Single(server => server.Id == "smp");
        }
        finally { release.TrySetResult(); if (running is not null) await running.WaitAsync(Deadline); }
        Pump(fixture.Window);
        Assert.True(fixture.Service.Snapshot.Instances.Single().Installed);
        Assert.Equal("none", fixture.Service.LastLaunchServerId);
        Assert.Equal("Running", fixture.Service.Snapshot.State);
        Assert.NotNull(fixture.Service.Snapshot.GameProcessId); Assert.False(fixture.Vm.IsBusy);
        Assert.False(Find<Grid>(fixture.Window, "HomeLaunchProgressPanel").IsEffectivelyVisible);
        Assert.True(Find<AccountsView>(fixture.Window, "HomeAccountsView").IsEffectivelyVisible);
        Assert.False(fixture.Vm.ShowOperationStatus); Assert.True(fixture.Vm.CanMutate);
    }

    [AvaloniaFact]
    public async Task FailedPreparationNeverLaunchesAndLeavesTheHomeActionRetryable()
    {
        var installs = 0;
        using var fixture = new Fixture(false, _ => { installs++; throw new InvalidDataException("fixture preparation failed"); });
        await fixture.ClickLaunch().WaitAsync(Deadline); Pump(fixture.Window);
        Assert.Equal(1, installs);
        Assert.Null(fixture.Service.LastLaunchServerId); Assert.Null(fixture.Service.Snapshot.GameProcessId);
        Assert.False(fixture.Service.Snapshot.Instances.Single().Installed);
        Assert.True(fixture.Vm.HasError); Assert.Contains("fixture preparation failed", fixture.Vm.ErrorMessage);
        Assert.False(fixture.Vm.IsBusy); Assert.True(fixture.Launch.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task CancellationPreventsLaunchEvenWhenTheInstallerReturnsSuccessfullyAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken installationToken = default;
        using var fixture = new Fixture(false, ct =>
        {
            installationToken = ct; entered.TrySetResult();
            // An uncancellable final installer stage must not bypass the pre-launch token check.
            return release.Task;
        });
        Task? running = null;
        try
        {
            running = fixture.ClickLaunch(); await entered.Task.WaitAsync(Deadline); Pump(fixture.Window);
            Assert.True(fixture.Vm.CancelOperationCommand.CanExecute(null));
            fixture.Vm.CancelOperationCommand.Execute(null);
            Assert.True(installationToken.IsCancellationRequested);
            Assert.Null(fixture.Service.LastLaunchServerId); Assert.Null(fixture.Service.Snapshot.GameProcessId);
        }
        finally { release.TrySetResult(); if (running is not null) await running.WaitAsync(Deadline); }
        Pump(fixture.Window);
        Assert.Null(fixture.Service.LastLaunchServerId); Assert.Null(fixture.Service.Snapshot.GameProcessId);
        Assert.False(fixture.Vm.IsBusy); Assert.False(fixture.Vm.HasError); Assert.Contains("取消", fixture.Vm.StateSummary);
    }

    [AvaloniaFact]
    public async Task AnInstalledInstanceLaunchesDirectlyWithoutRepeatingPreparation()
    {
        var installs = 0;
        using var fixture = new Fixture(true, _ => { installs++; throw new InvalidOperationException("Installed instances must not prepare again."); });
        await fixture.ClickLaunch().WaitAsync(Deadline); Pump(fixture.Window);
        Assert.Equal(0, installs); Assert.False(fixture.Vm.HasError);
        Assert.Equal("none", fixture.Service.LastLaunchServerId); Assert.Equal("Running", fixture.Service.Snapshot.State);
        Assert.NotNull(fixture.Service.Snapshot.GameProcessId);
    }

    private sealed class Fixture : IDisposable
    {
        public FakeLauncherService Service { get; } = new();
        public MainWindowViewModel Vm { get; }
        public MainWindow Window { get; }
        public Button Launch => Find<Button>(Window, "HomeLaunchButton");
        public Fixture(bool installed, Func<CancellationToken, Task> install)
        {
            var instance = Service.Snapshot.Instances[0] with
            {
                Name = "Imported Fabric fixture", VersionId = "fabric-loader-0.16.14-1.21.1",
                Loader = "fabric", LoaderVersion = "0.16.14", GameVersionId = "1.21.1", Installed = installed
            };
            Service.Publish(Service.Snapshot with { Instances = [instance], SelectedInstanceId = instance.Id });
            Service.InstallBehavior = install;
            Vm = new(Service, new FakeExternalLauncher()); Window = new() { DataContext = Vm };
            Window.Show(); Pump(Window);
        }
        public Task ClickLaunch()
        {
            var button = Launch;
            Assert.Same(Vm.Instances.LaunchCommand, button.Command); Assert.True(button.IsEffectivelyEnabled);
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(center); Window.MouseDown(center, MouseButton.Left); Window.MouseUp(center, MouseButton.Left); Pump(Window);
            var running = Vm.Instances.LaunchCommand.ExecutionTask; Assert.NotNull(running); return running;
        }
        public void Dispose() { Window.Close(); Vm.Dispose(); }
    }
}
