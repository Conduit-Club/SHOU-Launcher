using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfShellCompositionTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 0.5, expected + 0.5);

    [AvaloniaFact]
    public void HomeCardsMatchReferenceCompositionAndRetainDynamicServers()
    {
        var fake = new FakeLauncherService();
        fake.Publish(fake.Snapshot with { Servers = [new("none", "只启动游戏", ""), new("fixture-a", "服务器甲", "alpha.invalid"), new("fixture-b", "服务器乙", "beta.invalid"), new("fixture-c", "服务器丙", "gamma.invalid")] });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Home.SetPresentation(true, true);
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            Near(134, Find<Border>(window, "ClubIntroductionCard").Bounds.Height);
            Near(239, Find<Border>(window, "ClubServersCard").Bounds.Height);
            var intro = Find<Border>(window, "ClubIntroductionCard").TranslatePoint(default, window)!.Value;
            var servers = Find<Border>(window, "ClubServersCard").TranslatePoint(default, window)!.Value;
            Near(325, intro.X); Near(73, intro.Y); Near(222, servers.Y);
            Assert.Equal(4, Find<ListBox>(window, "ServerSelector").ItemCount);
            fake.Publish(fake.Snapshot with { Servers = [new("none", "只启动游戏", "")] }); Pump(window);
            Near(159, Find<Border>(window, "ClubServersCard").Bounds.Height);
            window.Width = 880; Pump(window);
            Assert.True(Find<Border>(window, "ClubIntroductionCard").Bounds.Height >= 134);
            Assert.True(Find<TextBlock>(window, "BrandTitle").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolCardsUseCompactReferenceRowsWithoutEnablingUnavailableActions()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        vm.ShowToolsCommand.Execute(null);
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window);
        try
        {
            Assert.True(vm.Tools.IsJavaTrust);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is "JoinLobbyCard" or "LobbyFooterCard");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Java 证书诊断与修复" && t.IsEffectivelyVisible);
            vm.Tools.ShowToolboxCommand.Execute(null); Pump(window);
            Near(95, Find<Border>(window, "ToolboxActionsCard").Bounds.Height);
            Near(240, Find<Border>(window, "CustomDownloadCard").Bounds.Height);
            Near(149, Find<Border>(window, "SkinDownloadCard").Bounds.Height);
            Near(84, Find<Border>(window, "ServerQueryCard").Bounds.Height);
            Assert.False(Find<Button>(window, "CustomDownloadButton").IsEffectivelyEnabled);
            Assert.Contains("尚未接入", Assert.IsType<string>(ToolTip.GetTip(Find<TextBlock>(window, "CustomDownloadTitle"))));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "自定义文件下载尚未迁移；当前可打开数据目录。" && t.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
}
