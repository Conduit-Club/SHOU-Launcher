using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;

namespace SHOU.Launcher.Desktop.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsJavaCandidates
{
    private static readonly string[] Roots =
    [
        @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE", @"SOFTWARE\Azul Systems\Zulu",
        @"SOFTWARE\BellSoft\Liberica", @"SOFTWARE\Eclipse Adoptium", @"SOFTWARE\AdoptOpenJDK",
        @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\Amazon\Corretto"
    ];

    public static IReadOnlyList<string> RegistryHomes()
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var root in Roots)
                    {
                        using var key = baseKey.OpenSubKey(root, writable: false);
                        if (key is not null) Visit(key, 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        return homes.Order(StringComparer.OrdinalIgnoreCase).ToArray();

        void Visit(RegistryKey key, int depth)
        {
            foreach (var name in new[] { "JavaHome", "InstallationPath", "Path", "Home" })
                if (key.GetValue(name) is string path && Path.IsPathFullyQualified(path)) homes.Add(path);
            if (depth >= 4) return;
            foreach (var name in key.GetSubKeyNames().Take(256))
            {
                using var child = key.OpenSubKey(name, writable: false);
                if (child is not null) Visit(child, depth + 1);
            }
        }
    }
}
