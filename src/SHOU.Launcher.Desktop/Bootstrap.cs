using System.Runtime.InteropServices;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;

namespace SHOU.Launcher.Desktop;

public static class Bootstrap
{
    public static ILauncherService? Launcher { get; internal set; }
    public static IExternalLauncher? ExternalLauncher { get; internal set; }
    public static string[] Arguments { get; internal set; } = [];
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
