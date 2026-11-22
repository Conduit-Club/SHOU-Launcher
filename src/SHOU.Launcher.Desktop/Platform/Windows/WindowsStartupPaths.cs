namespace SHOU.Launcher.Desktop.Platform.Windows;

public static class WindowsStartupPaths
{
    /// <summary>Explicit --data-dir wins. Existing desktop state takes priority over first-run legacy discovery.</summary>
    public static string? ResolveDataDirectory(string executableDirectory, string localDirectory)
    {
        var production = Path.Combine(localDirectory, "PCLCE", "shou.desktop.v1.json");
        if (File.Exists(production)) return null;
        var preview = Path.Combine(localDirectory, "SHOU-Launcher-Preview");
        return File.Exists(Path.Combine(preview, "local", "shou.desktop.v1.json")) ? preview : null;
    }
}
