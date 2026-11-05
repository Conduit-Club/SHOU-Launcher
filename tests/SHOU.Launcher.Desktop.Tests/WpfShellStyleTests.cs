using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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
    private static T Part<T>(Control control, string name) where T : Control => control.GetVisualDescendants().OfType<T>().Single(part => part.Name == name && ReferenceEquals(part.TemplatedParent, control));
    private static void State(Control control, string state)
    {
        control.IsEnabled = !state.StartsWith("disabled", StringComparison.Ordinal);
        var classes = (IPseudoClasses)control.Classes;
        foreach (var name in new[] { ":pointerover", ":pressed", ":focus", ":focus-visible", ":focus-within" }) classes.Remove(name);
        if (state is "pointerover" or "disabled-pointerover") classes.Add(":pointerover");
        if (state == "pressed") classes.Add(":pressed");
        if (state == "focus") foreach (var name in new[] { ":focus", ":focus-visible", ":focus-within" }) classes.Add(name);
    }

    [AvaloniaFact]
    public void CheckedControlPresentersAndGlyphsUseThemeColorsWithoutFadingDisabledText()
    {
        var checkBox = new CheckBox { Content = "自动保存", IsThreeState = true };
        var radio = new RadioButton { Content = "自动选择" };
        var window = new Window { Width = 420, Height = 160, Content = new StackPanel { Margin = new Thickness(16), Children = { checkBox, radio } } };
        window.Show(); Pump(window);
        try
        {
            foreach (var dark in new[] { false, true })
            {
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var foreground = dark ? "#F0DDEB" : "#5C3A54";
                var muted = dark ? "#BEA8B9" : "#927B8B";
                var surface = dark ? "#302634" : "#FCF6FB";
                var border = dark ? "#49384A" : "#EADDE6";
                var selection = dark ? "#233E4D" : "#EDF8FE";
                var selectionForeground = dark ? "#8ED6F4" : "#26799F";
                foreach (var isChecked in new bool?[] { false, true, null })
                {
                    checkBox.IsChecked = isChecked; radio.IsChecked = isChecked == true;
                    foreach (var state in new[] { "normal", "pointerover", "pressed", "focus", "disabled", "disabled-pointerover" })
                    {
                        State(checkBox, state); State(radio, state); Pump(window);
                        var disabled = !checkBox.IsEnabled;
                        foreach (var control in new Control[] { checkBox, radio })
                        {
                            ColorIs(disabled ? muted : foreground, ((TemplatedControl)control).Foreground);
                            ColorIs(disabled ? muted : foreground, Presenter(control).Foreground);
                            ColorIs(disabled ? muted : foreground, control.GetVisualDescendants().OfType<AccessText>().Single().Foreground);
                            Assert.Equal(1, control.Opacity);
                            Assert.Equal(!disabled, control.IsEffectivelyEnabled);
                        }
                        var rectangle = Part<Border>(checkBox, "NormalRectangle");
                        if (disabled)
                        {
                            ColorIs(surface, rectangle.Background); ColorIs(border, rectangle.BorderBrush);
                            ColorIs(muted, Part<Avalonia.Controls.Shapes.Path>(checkBox, "CheckGlyph").Fill);
                            ColorIs(muted, Part<Avalonia.Controls.Shapes.Ellipse>(radio, "CheckGlyph").Fill);
                        }
                        else if (isChecked != false)
                        {
                            ColorIs(selection, rectangle.Background); ColorIs(selectionForeground, rectangle.BorderBrush);
                            ColorIs(selectionForeground, Part<Avalonia.Controls.Shapes.Path>(checkBox, "CheckGlyph").Fill);
                        }
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InputTemplatePartsKeepReadableThemeColorsThroughHoverFocusAndDisable()
    {
        var combo = new ComboBox { ItemsSource = new[] { "自动", "手动" }, SelectedIndex = 0 };
        var numeric = new NumericUpDown { Value = 128, Minimum = 0, Maximum = 1024 };
        var textBox = new TextBox { Text = "启动参数" };
        var window = new Window { Width = 420, Height = 240, Content = new StackPanel { Margin = new Thickness(16), Spacing = 8, Children = { combo, numeric, textBox } } };
        window.Show(); Pump(window);
        try
        {
            var numericText = Part<TextBox>(numeric, "PART_TextBox");
            var spinner = Part<ButtonSpinner>(numeric, "PART_Spinner");
            foreach (var dark in new[] { false, true })
            {
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var foreground = dark ? "#F0DDEB" : "#5C3A54";
                var muted = dark ? "#BEA8B9" : "#927B8B";
                var sidebar = dark ? "#2B222F" : "#FFF9FC";
                var hover = dark ? "#493047" : "#FBE8F4";
                var surface = dark ? "#302634" : "#FCF6FB";
                var border = dark ? "#49384A" : "#EADDE6";
                var selection = dark ? "#233E4D" : "#EDF8FE";
                var focusBorder = dark ? "#8ED6F4" : "#26799F";
                foreach (var state in new[] { "normal", "pointerover", "pressed", "focus", "disabled", "disabled-pointerover" })
                {
                    State(combo, state); State(numeric, state); State(textBox, state);
                    var textClasses = (IPseudoClasses)numericText.Classes;
                    foreach (var name in new[] { ":pointerover", ":focus" }) textClasses.Remove(name);
                    if (state is "pointerover" or "disabled-pointerover") textClasses.Add(":pointerover");
                    if (state == "focus") textClasses.Add(":focus");
                    Pump(window);
                    var disabled = !combo.IsEnabled;
                    var expectedText = disabled ? muted : foreground;
                    ColorIs(expectedText, Part<ContentControl>(combo, "ContentPresenter").Foreground);
                    ColorIs(expectedText, combo.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "自动").Foreground);
                    ColorIs(expectedText, Part<PathIcon>(combo, "DropDownGlyph").Foreground);
                    ColorIs(expectedText, numericText.Foreground);
                    ColorIs(expectedText, Part<TextPresenter>(numericText, "PART_TextPresenter").Foreground);
                    ColorIs(expectedText, Part<TextPresenter>(textBox, "PART_TextPresenter").Foreground);
                    Assert.Equal((byte)0, Assert.IsAssignableFrom<ISolidColorBrush>(Part<Border>(numericText, "PART_BorderElement").Background).Color.A);
                    var comboBackground = Part<Border>(combo, "Background");
                    ColorIs(disabled ? surface : state == "pointerover" ? hover : state == "pressed" ? selection : sidebar, comboBackground.Background);
                    if (state == "focus")
                    {
                        var highlight = Part<Border>(combo, "HighlightBackground");
                        Assert.True(highlight.IsVisible); ColorIs(sidebar, highlight.Background); ColorIs(focusBorder, highlight.BorderBrush);
                    }
                    if (disabled)
                    {
                        ColorIs(border, comboBackground.BorderBrush);
                        ColorIs(surface, spinner.Background); ColorIs(border, spinner.BorderBrush);
                        ColorIs(surface, Part<Border>(textBox, "PART_BorderElement").Background);
                        ColorIs(border, Part<Border>(textBox, "PART_BorderElement").BorderBrush);
                        Assert.False(numericText.IsEffectivelyEnabled);
                    }
                    foreach (var control in new Control[] { combo, numeric, numericText, textBox }) Assert.Equal(1, control.Opacity);
                }
            }
        }
        finally { window.Close(); }
    }

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
                Assert.True(server.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Classes.Contains("serverSelectionCheck")).IsVisible);
                if (state.Length > 0) { ((IPseudoClasses)navigation.Classes).Remove(state); ((IPseudoClasses)server.Classes).Remove(state); }
            }
            var other = selector.GetVisualDescendants().OfType<ListBoxItem>().Single(item => !item.IsSelected);
            ColorIs("#5C3A54", other.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "社团生存").Foreground);
            Assert.False(other.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Classes.Contains("serverSelectionCheck")).IsVisible);
            selector.SelectedItem = other.DataContext; Pump(window);
            Assert.True(other.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Classes.Contains("serverSelectionCheck")).IsVisible);
            Assert.False(server.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(path => path.Classes.Contains("serverSelectionCheck")).IsVisible);
            var refresh = Find<Button>(window, "RefreshServersButton");
            Assert.Same(vm.Home.RefreshServersCommand, refresh.Command);
            Assert.Equal("刷新服务器状态", Avalonia.Automation.AutomationProperties.GetName(refresh));
            Assert.IsType<Avalonia.Controls.Shapes.Path>(refresh.Content);
            Assert.Equal(18, refresh.Bounds.Height);
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
            Assert.Equal(OperatingSystem.IsMacOS() ? WindowDecorations.Full : OperatingSystem.IsWindows() ? WindowDecorations.BorderOnly : WindowDecorations.None, window.WindowDecorations);
            Assert.Equal(48, Find<Border>(window, "LauncherTitleBar").Bounds.Height);
            Assert.Equal(!OperatingSystem.IsMacOS(), Find<StackPanel>(window, "CustomWindowControls").IsVisible);
            if (!OperatingSystem.IsMacOS()) return;
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.Equal(48, window.ExtendClientAreaTitleBarHeightHint);
            Assert.Equal(2, Grid.GetColumn(Find<StackPanel>(window, "WindowBrand")));
            Assert.Equal(16, Find<StackPanel>(window, "WindowBrand").Margin.Right);
            Assert.Equal(80, Find<Grid>(window, "ContextHeader").Margin.Left);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, KeyModifiers = KeyModifiers.Meta });
            Assert.Same(vm.Settings, vm.SelectedPage);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Meta });
            Assert.True(closed);
        }
        finally { if (!closed) window.Close(); }
    }
}
