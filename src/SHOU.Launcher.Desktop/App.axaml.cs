using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;

namespace SHOU.Launcher.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        Name = Bootstrap.Launcher?.Snapshot.Build.Name ?? new BuildConfiguration().GetIdentity().Name;
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow(Bootstrap.Launcher, Bootstrap.ExternalLauncher);
            Bootstrap.AttachLifetime(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static Window CreateMainWindow(ILauncherService? launcher, IExternalLauncher? externalLauncher)
    {
        if (launcher is not null && externalLauncher is not null)
        {
            try { return new MainWindow { DataContext = new MainWindowViewModel(launcher, externalLauncher) }; }
            catch (Exception ex) { return ErrorWindow("启动器服务初始化失败：" + UiPrivacy.Redact(ex.Message)); }
        }
        return ErrorWindow("启动器服务尚未连接，无法加载账户、实例与设置。请查看启动日志并重试。\n缺少宿主服务：" +
            (launcher is null ? "ILauncherService " : "") + (externalLauncher is null ? "IExternalLauncher" : ""));
    }

    private static Window ErrorWindow(string message) => new()
    {
        Name = "BootstrapFailureWindow", Title = "潮涌核心社启动器 · 初始化失败", Width = 680, Height = 320,
        Content = new StackPanel { Margin = new Thickness(32), Spacing = 18, Children =
        {
            new TextBlock { Text = "无法初始化启动器", FontSize = 24 },
            new TextBlock { Name = "HostErrorMessage", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
        } }
    };
}
