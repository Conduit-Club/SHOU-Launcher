using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfShellChromeTests
{
    [AvaloniaFact]
    public void CaptionMaximizeAndRestoreChangeStateAndAccessibleName()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            var maximize = Find<Button>(window, "MaximizeWindowButton");
            if (OperatingSystem.IsMacOS()) { Assert.False(maximize.IsEffectivelyVisible); return; }
            var initial = window.ClientSize;
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("还原", AutomationProperties.GetName(maximize));
                Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
                Assert.Equal(WindowDecorationsElementRole.MaximizeButton, WindowDecorationProperties.GetElementRole(maximize));
                Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(Find<Border>(window, "LauncherTitleBar")));
            }
            maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(initial, window.ClientSize);
            Assert.Equal("最大化", AutomationProperties.GetName(maximize));
        }
        finally { window.Close(); }
    }

    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    [AvaloniaFact]
    public void BrandAndFourRoutesFitTheBaselineAndMinimumTitleBar()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            foreach (var width in new[] { 976d, 880d })
            {
                window.Width = width; Pump(window);
                var titleBar = Find<Border>(window, "LauncherTitleBar");
                var navigation = Find<StackPanel>(window, "PrimaryNavigation");
                var routes = navigation.Children.OfType<ToggleButton>().ToArray();
                Assert.Equal(4, routes.Length); Assert.Equal(48, titleBar.Bounds.Height);
                var first = Bounds(routes[0], window); var last = Bounds(routes[^1], window);
                var brand = Bounds(Find<StackPanel>(window, "WindowBrand"), window);
                Assert.All(routes, route =>
                {
                    var bounds = Bounds(route, window);
                    Assert.True(route.IsEffectivelyVisible);
                    Assert.InRange(bounds.Top, 0, 48 - bounds.Height);
                    Assert.InRange(bounds.Left, 0, width - bounds.Width);
                });
                if (OperatingSystem.IsMacOS())
                {
                    Assert.InRange((first.Left + last.Right) / 2, width / 2 - .5, width / 2 + .5);
                    Assert.True(first.Left >= 80);
                    Assert.True(brand.Left >= last.Right + 12);
                    Assert.InRange(brand.Right, width - 16.5, width - 15.5);
                    Assert.False(Find<StackPanel>(window, "CustomWindowControls").IsVisible);
                }
                else
                {
                    Assert.InRange(brand.Left, 15.5, 16.5);
                    Assert.True(brand.Right <= first.Left);
                    Assert.True(last.Right <= Bounds(Find<StackPanel>(window, "CustomWindowControls"), window).Left);
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ContextTitleLeavesCaptionSpaceAndTrimsLongInstanceNames()
    {
        var service = new FakeLauncherService();
        var instance = service.Snapshot.Instances[0] with { Name = new string('界', 120) };
        service.Publish(service.Snapshot with { Instances = [instance], SelectedInstanceId = instance.Id });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        vm.Instances.ShowInstanceSettingsCommand.Execute(null);
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            foreach (var width in new[] { 976d, 880d })
            {
                window.Width = width; Pump(window);
                var header = Find<Grid>(window, "ContextHeader");
                var title = Find<TextBlock>(window, "ContextTitle");
                var back = Find<Button>(window, "BackNavigationButton");
                Assert.True(header.IsEffectivelyVisible); Assert.True(back.IsEffectivelyVisible);
                Assert.False(Find<StackPanel>(window, "WindowBrand").IsEffectivelyVisible);
                Assert.False(Find<StackPanel>(window, "PrimaryNavigation").IsEffectivelyVisible);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(vm.NavigationTitle, title.Text);
                Assert.True(Bounds(title, window).Left >= Bounds(back, window).Right + 5.5);
                if (OperatingSystem.IsMacOS())
                {
                    Assert.Equal(3, Grid.GetColumnSpan(header));
                    Assert.True(Bounds(back, window).Left >= 80);
                    Assert.True(Bounds(title, window).Right <= width - 15.5);
                }
                else
                    Assert.True(Bounds(title, window).Right <= Bounds(Find<StackPanel>(window, "CustomWindowControls"), window).Left);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HomeKeepsOnlyTheReferenceLaunchFooterAtTheMinimumWindowSize()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 560 };
        window.Show(); Pump(window);
        try
        {
            var footer = Find<StackPanel>(window, "HomeLaunchActions");
            var buttons = footer.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(3, buttons.Length);
            Assert.Same(vm.Instances.LaunchCommand, Find<Button>(window, "HomeLaunchButton").Command);
            Assert.Same(vm.ShowInstancesCommand, Find<Button>(window, "HomeSelectInstanceButton").Command);
            Assert.Same(vm.Instances.ShowInstanceSettingsCommand, Find<Button>(window, "HomeInstanceSettingsButton").Command);
            Assert.All(buttons, button =>
            {
                Assert.True(button.IsEffectivelyVisible);
                var bounds = Bounds(button, window);
                Assert.InRange(bounds.Left, 19.5, 280 - bounds.Width + .5);
                Assert.True(bounds.Bottom <= 540.5);
            });
            Assert.InRange(Bounds(Find<Button>(window, "HomeSelectInstanceButton"), window).Bottom, 539.5, 540.5);
            Assert.Same(vm.Accounts, Find<AccountsView>(window, "HomeAccountsView").DataContext);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Expander>(), expander => expander.Name == "AccountSecurityDetails");
        }
        finally { window.Close(); }
    }
}
