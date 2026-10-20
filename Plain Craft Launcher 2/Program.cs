using System.Diagnostics;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class Program
{
    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        Console.WriteLine("Welcome to Plain Craft Launcher 2 Community Edition!");
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            try
            {
                return new FormMain();
            }
            catch (Exception ex)
            {
                // If XAML construction fails, the lifecycle service catches the exception and
                // would otherwise leave the startup splash visible while continuing without a window.
                try
                {
                    var splash = ModMain.frmStart;
                    ModMain.frmStart = null;
                    splash?.Close(TimeSpan.Zero);
                }
                catch (Exception closeException)
                {
                    ModBase.Log(closeException, "[Start] 关闭启动画面失败", ModBase.LogLevel.Debug);
                }

                ModBase.Log(ex, "[Start] 主窗体创建失败，启动已终止", ModBase.LogLevel.Debug);
                Lifecycle.ForceShutdown((int)ModBase.ProcessReturnValues.Exception);
                throw;
            }
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }
}
