namespace SHOU.Launcher.Desktop.Tests;

internal static class FixturePaths
{
    public static string Create(string area) => Path.Combine(
        Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? Path.GetTempPath(),
        "test-data", "desktop", area, Guid.NewGuid().ToString("N"));
}
