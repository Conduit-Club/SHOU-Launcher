using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class SharedPresentationTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);

    [AvaloniaFact]
    public void EveryBundledIconUsesFiniteGeometryWithinTheOriginalViewport()
    {
        foreach (var kind in ClubIcon.AvailableKinds)
        {
            var shapes = ClubIcon.GetShapes(kind); Assert.NotEmpty(shapes);
            var bounds = shapes.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
            Assert.True(double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height));
            Assert.InRange(bounds.Left, -1, 24); Assert.InRange(bounds.Top, -1, 24);
            Assert.InRange(bounds.Right, 0, 25); Assert.InRange(bounds.Bottom, 0, 25);
        }
        Assert.Empty(ClubIcon.GetShapes("../../outside"));
    }

    [AvaloniaFact]
    public void SettingsNavigationAlignsIconAndLabelAcrossEverySectionAndTheme()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var section in SettingsPageViewModel.SectionIds)
            {
                window.RequestedThemeVariant = theme; vm.Settings.SelectedSection = section; Pump(window);
                foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("contextNavigation")))
                {
                    var grid = Assert.IsType<Grid>(button.Content);
                    var icon = Assert.Single(grid.Children.OfType<ClubIcon>());
                    var label = Assert.Single(grid.Children.OfType<TextBlock>());
                    Assert.Equal(new Size(20, 20), icon.Bounds.Size); Assert.Equal(14, label.FontSize); Assert.Equal(20, label.LineHeight);
                    Assert.InRange(Math.Abs(icon.Bounds.Center.Y - label.Bounds.Center.Y), 0, .5);
                    Assert.Equal(10, label.Bounds.Left - icon.Bounds.Right);
                    Assert.NotNull(icon.Foreground);
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SharedInputsAndButtonsContainChineseAndLatinTextWithoutClipping()
    {
        var text = new TextBox { Text = "Java 路径 中文 / Library" };
        var combo = new ComboBox { ItemsSource = new[] { "自动 Java 21" }, SelectedIndex = 0 };
        var button = new Button { Content = "选择 Java…" };
        var numeric = new NumericUpDown { Value = 2048 };
        var check = new CheckBox { Content = "自动修复 Java 证书", IsChecked = true };
        var window = new Window { Width = 880, Height = 560, Content = new StackPanel { Margin = new Thickness(24), Spacing = 10, Children = { text, combo, button, numeric, check } } };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(28, text.Bounds.Height); Assert.Equal(text.Bounds.Height, combo.Bounds.Height); Assert.Equal(text.Bounds.Height, button.Bounds.Height);
            Assert.Equal(text.Bounds.Height, numeric.Bounds.Height);
            var presenter = text.GetVisualDescendants().OfType<TextPresenter>().Single();
            Assert.InRange(presenter.Bounds.Height, 16, text.Bounds.Height - 2);
            var checkboxText = check.GetVisualDescendants().OfType<ContentPresenter>().Single(x => x.Name == "PART_ContentPresenter");
            Assert.True(checkboxText.Margin.Left >= 8);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DefaultPagesDoNotAddMigrationInstructionsOrHealthySuccessParagraphs()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            vm.ShowSettingsCommand.Execute(null); Pump(window);
            Assert.False(Find<TextBlock>(window, "SettingsDraftStatus").IsEffectivelyVisible);
            foreach (var section in SettingsPageViewModel.SectionIds)
            { vm.Settings.SelectedSection = section; Pump(window); NoMigrationParagraphs(); }
            vm.ShowToolsCommand.Execute(null); Pump(window);
            Assert.False(Find<TextBlock>(window, "JavaTrustStatusText").IsEffectivelyVisible);
            vm.Tools.ShowToolboxCommand.Execute(null); Pump(window); NoMigrationParagraphs();
            vm.ShowDownloadsCommand.Execute(null); Pump(window); NoMigrationParagraphs();
            Assert.False(vm.Downloads.ShowRequestStatus);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible), t => t.Text == vm.Downloads.CatalogPurpose);
        }
        finally { window.Close(); }
        void NoMigrationParagraphs() => Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? ""), text =>
            text.Contains("尚未迁移") || text.Contains("尚未接入") || text.Contains("当前启动使用经过验证的参数"));
    }
}
