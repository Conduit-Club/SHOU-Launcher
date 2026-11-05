using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Themes;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfShellStyleTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static ContentPresenter Presenter(Control control) => control.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, control));
    private static void ColorIs(string hex, IBrush? brush) => Assert.Equal(Color.Parse(hex), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);

    [AvaloniaFact]
    public void SelectedNavigationAndServerCardsKeepClubColorsInEveryPointerState()
    {
        foreach (var key in new[] { "overview", "settings", "modify", "export", "worlds", "screenshots", "mods", "resourcepacks", "shaders", "schematics", "servers", "relations" })
        {
            var bounds = Geometry.Parse(ClubIcons.ForInstanceSection(key)).Bounds;
            Assert.True(double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0);
        }
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var fake = new FakeLauncherService();
        fake.Publish(fake.Snapshot with { Settings = fake.Snapshot.Settings with { Theme = "Light" } });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Home.SetPresentation(true, true);
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            var navigation = Find<ToggleButton>(window, "LaunchNavigationButton");
            var selector = Find<ListBox>(window, "ServerSelector");
            var server = selector.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsSelected);
            foreach (var state in new[] { "", ":pointerover", ":pressed" })
            {
                if (state.Length > 0) { ((IPseudoClasses)navigation.Classes).Add(state); ((IPseudoClasses)server.Classes).Add(state); }
                Pump(window);
                ColorIs("#FFFFFF", Presenter(navigation).Background);
                ColorIs("#26799F", Presenter(navigation).Foreground);
                ColorIs("#26799F", navigation.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "启动").Foreground);
                ColorIs("#EDF8FE", Presenter(server).Background);
                ColorIs("#AC2F6C", Presenter(server).BorderBrush);
                ColorIs("#5C3A54", Presenter(server).Foreground);
                ColorIs("#5C3A54", server.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "只启动游戏").Foreground);
                if (state.Length > 0) { ((IPseudoClasses)navigation.Classes).Remove(state); ((IPseudoClasses)server.Classes).Remove(state); }
            }
            var other = selector.GetVisualDescendants().OfType<ListBoxItem>().Single(item => !item.IsSelected);
            ColorIs("#5C3A54", other.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "社团生存").Foreground);
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = previousTheme; }
    }

    [AvaloniaFact]
    public void PlatformChromeReservesNativeCaptionAndSupportsMacWindowShortcuts()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm };
        var closed = false; window.Closed += (_, _) => closed = true;
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(OperatingSystem.IsMacOS() ? WindowDecorations.Full : WindowDecorations.None, window.WindowDecorations);
            Assert.Equal(48, Find<Border>(window, "LauncherTitleBar").Bounds.Height);
            Assert.Equal(!OperatingSystem.IsMacOS(), Find<StackPanel>(window, "CustomWindowControls").IsVisible);
            if (!OperatingSystem.IsMacOS()) return;
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.Equal(48, window.ExtendClientAreaTitleBarHeightHint);
            Assert.Equal(80, Find<StackPanel>(window, "WindowBrand").Margin.Left);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, KeyModifiers = KeyModifiers.Meta });
            Assert.Same(vm.Settings, vm.SelectedPage);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Meta });
            Assert.True(closed);
        }
        finally { if (!closed) window.Close(); }
    }
}
