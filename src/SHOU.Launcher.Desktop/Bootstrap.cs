using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Desktop;

public static partial class Bootstrap
{
    public static ILauncherService? Launcher { get; internal set; }
    public static IExternalLauncher? ExternalLauncher { get; internal set; }
    public static string[] Arguments { get; internal set; } = [];
    private static string exitReason = "normal_close";
    private static bool nativeWindowObserved;
    public static void AttachLifetime(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is { } window)
            window.Opened += (_, _) => { nativeWindowObserved = window.TryGetPlatformHandle() is { Handle: var h } && h != IntPtr.Zero; WriteNativeReport("window_opened"); };
        desktop.Exit += (_, _) => WriteNativeReport(exitReason);
        if (int.TryParse(ArgumentValue("--exit-after"), out var seconds) && seconds is > 0 and <= 3600)
            DispatcherTimer.RunOnce(() => { exitReason = "exit_after"; desktop.Shutdown(0); }, TimeSpan.FromSeconds(seconds));
    }
    internal static string? ArgumentValue(string name)
    {
        var index = Array.IndexOf(Arguments, name);
        return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : null;
    }
    internal static void WriteNativeReport(string reason)
    {
        var result = ArgumentValue("--result-file");
        if (result is null) return;
        if (!Path.IsPathFullyQualified(result)) throw new ArgumentException("Result file must be absolute.");
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        var report = new { runId = ArgumentValue("--run-id"), build = new BuildConfiguration().GetIdentity(),
            os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            configurationAssembly = typeof(BuildConfiguration).Assembly.Location, dataDirectory = Launcher?.Paths.DataDirectory,
            isolated = Launcher?.Paths.IsIsolated, state = Launcher?.Snapshot.State, nativeWindowHandleObserved = nativeWindowObserved,
            reason, reportedAt = DateTimeOffset.UtcNow, evidenceBoundary = "application observation only; external GUI evidence required" };
        var temporary = result + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, result, true);
    }
    public static int? TryRunDiagnostics(string[] args)
    {
        Arguments = args;
        if (!args.Contains("--diagnostics", StringComparer.Ordinal)) return null;
        Console.WriteLine(JsonSerializer.Serialize(new { build = new BuildConfiguration().GetIdentity(),
            os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            settingsKeys = GeneratedSettingsSchema.Entries, startupOrder = GeneratedLifecycle.Entries }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
