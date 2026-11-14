using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using SHOU.Launcher.Desktop;

[assembly: AvaloniaTestApplication(typeof(SHOU.Launcher.Desktop.Tests.HeadlessApp))]

namespace SHOU.Launcher.Desktop.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
