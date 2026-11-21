namespace SHOU.Launcher.Desktop.Platform;

public sealed partial class JavaDiscovery
{
    private IEnumerable<string> PortableSearchRoots()
    {
        foreach (var root in _additionalSearchRoots) yield return root;
        if (!_includeSystemSources) yield break;
        yield return _home;
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(_home, ".jdks");
        yield return Path.Combine(_home, ".sdkman", "candidates", "java");
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome) && Path.IsPathFullyQualified(javaHome))
            yield return Path.GetDirectoryName(javaHome.TrimEnd(Path.DirectorySeparatorChar)) ?? javaHome;
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var directory in EnumerateDirectories(drive.RootDirectory.FullName).Where(d => IsJavaSearchDirectory(Path.GetFileName(d))))
                    yield return directory;
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[] { "/opt/homebrew/opt", "/usr/local/opt", "/Library/Java/JavaVirtualMachines", Path.Combine(_home, "Library", "Java", "JavaVirtualMachines") }) yield return root;
        }
    }

    private static bool IsJavaSearchDirectory(string name)
    {
        if (name is ".git" or "node_modules" or "Downloads" or "Pictures" or "Music" or "Documents" || name.Contains("cache", StringComparison.OrdinalIgnoreCase)) return false;
        return new[] { "java", "jdk", "jre", "jbr", "runtime", "openjdk", "graal", "zulu", "corretto", "adoptium", "temurin", "liberica", "bellsoft", "microsoft", "jetbrains", "android", "studio", "program", "software", "tools", "sdk", "minecraft", "pcl", "home", "contents", "bin" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
            || name.Length <= 30 && name.Any(char.IsDigit) && name.All(c => char.IsDigit(c) || c is '.' or '_' or '-' or '+');
    }
}
