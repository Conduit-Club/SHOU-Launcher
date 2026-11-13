namespace SHOU.Launcher.Desktop.Platform.Windows;

public static class WindowsStartupPaths
{
    /// <summary>Explicit --data-dir always wins. Preserve an existing preview unless using a legacy installation.</summary>
    public static string? ResolveDataDirectory(string executableDirectory, string localDirectory)
    {
        var production = Path.Combine(localDirectory, "PCLCE", "shou.desktop.v1.json");
        if (File.Exists(production)) return null;
        if (Directory.Exists(Path.Combine(executableDirectory, "PCL")) || Directory.Exists(Path.Combine(executableDirectory, "versions"))
            || Directory.Exists(Path.Combine(executableDirectory, ".minecraft", "versions"))) return null;
        var preview = Path.Combine(localDirectory, "SHOU-Launcher-Preview");
        return File.Exists(Path.Combine(preview, "local", "shou.desktop.v1.json")) ? preview : null;
    }
}
