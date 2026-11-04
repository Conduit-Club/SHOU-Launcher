using Avalonia;

namespace SHOU.Launcher.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return Bootstrap.Run(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
