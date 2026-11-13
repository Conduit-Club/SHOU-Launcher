namespace PCL.Core.Logging;

// Legacy analysis logging routes to the host diagnostic trace; no WPF logging service is loaded.
internal static class LogWrapper
{
    public static void Info(string category, string text) => System.Diagnostics.Trace.WriteLine(new SHOU.Launcher.Core.Diagnostics.LogRedactor().Scrub(text), category);
    public static void Warn(Exception error, string category, string text) => Info(category, text + " (" + error.GetType().Name + ")");
    public static void Error(Exception error, string category, string text) => Warn(error, category, text);
}
