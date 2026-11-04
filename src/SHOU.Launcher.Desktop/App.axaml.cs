using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window { Title = new BuildConfiguration().GetIdentity().Name,
                Width = 1000, Height = 700, Content = new TextBlock { Margin = new Thickness(24),
                Text = new BuildConfiguration().GetIdentity().Version + " · B0 共享契约已加载；业务接线待集成。" } };
        base.OnFrameworkInitializationCompleted();
    }
}
