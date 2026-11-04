using Avalonia;

namespace SHOU.Launcher.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var diagnostic = Bootstrap.TryRunDiagnostics(args);
        if (diagnostic.HasValue) return diagnostic.Value;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
