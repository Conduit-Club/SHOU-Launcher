using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfShellLayoutTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ShellUsesReferenceGeometryAndFourPrimaryRoutes()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(976, window.Width);
            Assert.Equal(636, window.Height);
            Assert.Equal(48, Find<Border>(window, "LauncherTitleBar").Bounds.Height);
            Assert.Equal(300, Find<Border>(window, "LaunchAccountPanel").Bounds.Width);
            var navigation = Find<StackPanel>(window, "PrimaryNavigation");
            Assert.Equal(4, navigation.Children.OfType<ToggleButton>().Count());
            var launch = Find<ToggleButton>(window, "LaunchNavigationButton");
            launch.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            launch.Command!.Execute(null); Pump(window);
            Assert.True(launch.IsChecked);
            Find<ToggleButton>(window, "DownloadsNavigationButton").Command!.Execute(null); Pump(window);
            Assert.Same(vm.Downloads, vm.SelectedPage);
            Find<ToggleButton>(window, "SettingsNavigationButton").Command!.Execute(null); Pump(window);
            Assert.Same(vm.Settings, vm.SelectedPage);
            Find<ToggleButton>(window, "ToolsNavigationButton").Command!.Execute(null); Pump(window);
            Assert.Contains(window.GetVisualDescendants(), v => v is ToolsView);
            Assert.False(Find<Border>(window, "OperationBar").IsVisible);
            vm.ShowLogsCommand.Execute(null); Pump(window);
            Assert.True(Find<Button>(window, "BackNavigationButton").IsEffectivelyVisible);
            Find<Button>(window, "BackNavigationButton").Command!.Execute(null); Pump(window);
            Assert.Same(vm.Settings, vm.SelectedPage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HomeBindsRealLaunchSelectionAndServerRefresh()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Home.SetPresentation(true, true);
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal("潮涌核心社", Find<TextBlock>(window, "BrandTitle").Text);
            Assert.Same(vm.Instances.LaunchCommand, Find<Button>(window, "HomeLaunchButton").Command);
            Assert.True(Find<Button>(window, "HomeLaunchButton").IsEffectivelyEnabled);
            await vm.Home.RefreshServersCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal("7", vm.Home.OnlinePlayerSummary);
            Find<ListBox>(window, "ServerSelector").SelectedItem = vm.Home.Servers.Single(s => s.Id == "smp"); Pump(window);
            Assert.False(Find<Button>(window, "HomeLaunchButton").IsEffectivelyEnabled);
            vm.Home.SetPresentation(false, false, false, 0.6); Pump(window);
            Assert.False(Find<ListBox>(window, "ServerSelector").IsEffectivelyVisible);
            Assert.Equal(0.6, window.Opacity);
            Assert.Equal("smp", vm.Home.SelectedServerId);
            Find<Button>(window, "HomeSelectInstanceButton").Command!.Execute(null); Pump(window);
            Assert.Same(vm.Instances, vm.SelectedPage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolsRenderHonestUnavailableActionsAndRealNavigationWithoutBindingFailures()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var previousSink = Logger.Sink;
        var sink = new BindingFailures();
        Logger.Sink = sink;
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            vm.ShowToolsCommand.Execute(null); Pump(window);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is "JoinLobbyButton" or "CreateLobbyButton" or "LobbyNavigationButton");
            Assert.Same(vm.Settings.InspectJavaTrustCommand, Find<Button>(window, "InspectJavaTrustButton").Command);
            Find<ToggleButton>(window, "ToolboxNavigationButton").Command!.Execute(null); Pump(window);
            Assert.True(vm.Tools.IsToolbox);
            var toolbox = Find<ToggleButton>(window, "ToolboxNavigationButton");
            toolbox.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            toolbox.Command!.Execute(null); Pump(window);
            Assert.True(toolbox.IsChecked);
            Assert.False(Find<Button>(window, "CustomDownloadButton").IsEffectivelyEnabled);
            Find<Expander>(window, "AchievementGenerator").IsExpanded = true;
            Find<Expander>(window, "AvatarGenerator").IsExpanded = true; Pump(window);
            Assert.Contains("尚未迁移", vm.Tools.ToolboxCapabilityReason);
            Assert.Empty(sink.Errors);
        }
        finally { window.Close(); Logger.Sink = previousSink; }
    }

    private sealed class BindingFailures : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) { if (IsEnabled(level, area)) Errors.Add(messageTemplate); }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Errors.Add(messageTemplate + " " + string.Join(" | ", propertyValues.Select(p => p?.ToString())));
        }
    }
}
