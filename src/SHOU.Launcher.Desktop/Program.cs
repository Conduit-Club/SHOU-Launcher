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
        var behavior = Bootstrap.Launcher?.Snapshot.Settings.Launcher ?? new SHOU.Launcher.Core.Abstractions.LauncherBehavior();
        if (OperatingSystem.IsWindows() && behavior.DisableHardwareAcceleration)
            builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        // Keep popup menus and selectors in the same accessible AppKit window as their owner.
        if (OperatingSystem.IsMacOS())
        {
            var options = new AvaloniaNativePlatformOptions { OverlayPopups = true };
            if (behavior.DisableHardwareAcceleration) options.RenderingMode = [AvaloniaNativeRenderingMode.Software];
            builder.With(options);
        }
        return builder;
    }
}
