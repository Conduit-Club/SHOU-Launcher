using Avalonia;

namespace SHOU.Launcher.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return Bootstrap.Run(args);
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Keep popup menus and selectors in the same accessible AppKit window as their owner.
        if (OperatingSystem.IsMacOS()) builder.With(new AvaloniaNativePlatformOptions { OverlayPopups = true });
        return builder;
    }
}
