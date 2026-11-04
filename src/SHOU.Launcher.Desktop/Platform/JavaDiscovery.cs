using System.Security;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class JavaDiscovery : IJavaDiscovery
{
    private readonly IProcessRunner _processes;
    private readonly JavaRuntimeProbe _probe;
    private readonly PlatformContext _platform;
    private readonly string? _configuredExecutable;
    private readonly string _home;
    private readonly bool _includeSystemSources;

    public JavaDiscovery(IProcessRunner processes, PlatformContext platform, PlatformOptions? options = null,
        string? homeDirectory = null, bool includeSystemSources = true, TimeSpan? probeTimeout = null)
    {
        _processes = processes;
        _probe = new JavaRuntimeProbe(processes, probeTimeout);
        _platform = platform;
        _configuredExecutable = options?.ConfiguredJavaExecutable;
        _home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _includeSystemSources = includeSystemSources;
    }

    public Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default) =>
        _probe.ProbeAsync(executable, cancellationToken);

    public async Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var candidates = new Dictionary<string, string>(_platform.OS == LauncherOS.Windows
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var configured = request.ConfiguredExecutable ?? _configuredExecutable;
        if (!string.IsNullOrWhiteSpace(configured)) Add(configured, "configured", requireExists: false);
        ScanRuntimeRoot(request.ManagedRuntimeDirectory, "managed");

        if (_includeSystemSources)
        {
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome)) AddHome(javaHome, "JAVA_HOME");
            if (_platform.OS == LauncherOS.MacOS && OperatingSystem.IsMacOS())
            {
                // Finder does not inherit an interactive shell's PATH.
                foreach (var root in new[] { "/Library/Java/JavaVirtualMachines", Path.Combine(_home, "Library", "Java", "JavaVirtualMachines") })
                    foreach (var directory in EnumerateDirectories(root))
                        AddHome(Path.Combine(directory, "Contents", "Home"), "JavaVirtualMachines");

                if (File.Exists("/usr/libexec/java_home"))
                {
                    var result = await _processes.RunAsync(new ProcessSpec("/usr/libexec/java_home", ["-V"], "/",
                        Timeout: TimeSpan.FromSeconds(5), OutputLimitCharacters: 32768), cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.ExitCode == 0 && !result.TimedOut && !result.OutputTruncated)
                        foreach (var line in (result.StandardOutput + "\n" + result.StandardError).Split('\n'))
                        {
                            var slash = line.IndexOf('/');
                            if (slash >= 0) AddHome(line[slash..].Trim(), "java_home");
                        }
                }
            }
            else if (_platform.OS == LauncherOS.Windows && OperatingSystem.IsWindows())
            {
                foreach (var home in WindowsJavaCandidates.RegistryHomes()) AddHome(home, "registry");
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                ScanRuntimeRoot(Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"), "MicrosoftStore");
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                ScanRuntimeRoot(Path.Combine(roaming, ".minecraft", "runtime"), "MinecraftRuntime");
                foreach (var baseDirectory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
                    foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto" })
                        ScanRuntimeRoot(Path.Combine(baseDirectory, vendor), "installed");
            }
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                _platform.OS == LauncherOS.Windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = _platform.OS == LauncherOS.Windows ? entry.Trim('"') : entry;
                if (Path.IsPathFullyQualified(directory)) Add(Path.Combine(directory, JavaFilename), "PATH");
            }
        }

        var installations = new List<JavaInstallation>();
        foreach (var (path, source) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            installations.Add((await _probe.ProbeAsync(path, cancellationToken)) with { Source = source });
        }
        return installations.OrderByDescending(java => java.IsUsable
            && (!request.RequiredMajorVersion.HasValue || java.MajorVersion == request.RequiredMajorVersion)
            && (!request.RequiredArchitecture.HasValue || java.RuntimeArchitecture == request.RequiredArchitecture))
            .ToArray();

        void Add(string path, string source, bool requireExists = true)
        {
            if (candidates.Count >= 64) return;
            if (!Path.IsPathFullyQualified(path))
            {
                // Keep an explicitly configured bad path visible to the caller; never resolve it against cwd.
                if (!requireExists) candidates.TryAdd(path, source);
                return;
            }
            path = Path.GetFullPath(path);
            // This Apple stub can invoke a system installation prompt rather than an installed JVM.
            if (_platform.OS == LauncherOS.MacOS && path == "/usr/bin/java") return;
            if (!requireExists || File.Exists(path)) candidates.TryAdd(path, source);
        }

        void AddHome(string path, string source)
        {
            if (!Path.IsPathFullyQualified(path)) return;
            Add(Path.Combine(path, "bin", JavaFilename), source);
            if (_platform.OS == LauncherOS.MacOS) Add(Path.Combine(path, "Contents", "Home", "bin", JavaFilename), source);
        }

        void ScanRuntimeRoot(string root, string source)
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return;
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 2048)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = queue.Dequeue();
                AddHome(directory, source);
                if (Path.GetFileName(directory) == "bin") Add(Path.Combine(directory, JavaFilename), source);
                if (depth >= 7) continue;
                foreach (var child in EnumerateDirectories(directory))
                {
                    if (IsLink(child)) continue; // No loops or traversal into unrelated runtime trees.
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    private string JavaFilename => _platform.OS == LauncherOS.Windows ? "java.exe" : "java";

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return true; }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root).Order(StringComparer.Ordinal).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return []; }
    }
}
