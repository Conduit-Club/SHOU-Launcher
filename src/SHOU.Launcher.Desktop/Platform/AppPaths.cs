using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform.MacOS;

namespace SHOU.Launcher.Desktop.Platform;

/// <summary>Calculates paths without creating directories or opening a credential store.</summary>
public sealed class AppPaths : IAppPaths
{
    public string DataDirectory { get; }
    public string SharedDataDirectory { get; }
    public string LocalDataDirectory { get; }
    public string CacheDirectory { get; }
    public string LogsDirectory { get; }
    public string RuntimesDirectory { get; }
    public string InstancesDirectory { get; }
    public string TempDirectory { get; }
    public string ResourcesDirectory { get; }
    public string LockFile { get; }
    public string CredentialScope { get; }
    public bool IsIsolated { get; }

    public AppPaths(PlatformOptions options, PlatformContext context, string? executableDirectory = null,
        string? homeDirectory = null, string? roamingDataDirectory = null, string? localDataDirectory = null,
        string? temporaryDirectory = null)
    {
        ResourcesDirectory = Absolute(executableDirectory ?? AppContext.BaseDirectory);
        var home = Absolute(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        IsIsolated = !string.IsNullOrWhiteSpace(options.DataDirectory);
        if (IsIsolated)
        {
            DataDirectory = ResolveExistingLinks(Absolute(options.DataDirectory!));
            SharedDataDirectory = Path.Combine(DataDirectory, "shared");
            LocalDataDirectory = Path.Combine(DataDirectory, "local");
            CacheDirectory = Path.Combine(DataDirectory, "cache");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            TempDirectory = Path.Combine(DataDirectory, "tmp");
            var identity = context.OS == LauncherOS.Windows ? DataDirectory.ToUpperInvariant() : DataDirectory;
            CredentialScope = "com.conduitclub.shou-launcher.isolated."
                + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }
        else if (context.OS == LauncherOS.Windows)
        {
            // These are the production roots and lock used by the legacy WPF launcher.
            DataDirectory = Path.Combine(ResourcesDirectory, "PCL");
            SharedDataDirectory = Path.Combine(Absolute(roamingDataDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), "PCLCE");
            LocalDataDirectory = Path.Combine(Absolute(localDataDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "PCLCE");
            CacheDirectory = Path.Combine(LocalDataDirectory, "cache");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            TempDirectory = Path.Combine(Absolute(temporaryDirectory ?? Path.GetTempPath()), "PCLCE");
            CredentialScope = "com.conduitclub.shou-launcher";
        }
        else if (context.OS == LauncherOS.MacOS)
        {
            DataDirectory = Path.Combine(home, "Library", "Application Support", "SHOU-Launcher");
            SharedDataDirectory = LocalDataDirectory = DataDirectory;
            CacheDirectory = Path.Combine(home, "Library", "Caches", "SHOU-Launcher");
            LogsDirectory = Path.Combine(home, "Library", "Logs", "SHOU-Launcher");
            TempDirectory = Path.Combine(Absolute(temporaryDirectory ?? Path.GetTempPath()), "SHOU-Launcher");
            CredentialScope = "com.conduitclub.shou-launcher";
        }
        else if (context.OS == LauncherOS.Linux)
        {
            DataDirectory = Path.Combine(home, ".local", "share", "SHOU-Launcher");
            SharedDataDirectory = LocalDataDirectory = DataDirectory;
            CacheDirectory = Path.Combine(home, ".cache", "SHOU-Launcher");
            LogsDirectory = Path.Combine(DataDirectory, "logs");
            TempDirectory = Path.Combine(Absolute(temporaryDirectory ?? Path.GetTempPath()), "SHOU-Launcher");
            CredentialScope = "com.conduitclub.shou-launcher";
        }
        else throw new PlatformNotSupportedException("This operating system has no application path mapping.");

        RuntimesDirectory = Path.Combine(DataDirectory, "runtimes");
        InstancesDirectory = Path.Combine(DataDirectory, "instances");
        if (OperatingSystem.IsMacOS())
        {
            // Normalize stored case/Unicode through the actual filesystem, including ancestors of new roots.
            // Do not merge Foo/foo or Unicode spellings on a filesystem where they identify distinct objects.
            DataDirectory = ResolveExistingLinks(DataDirectory);
            SharedDataDirectory = ResolveExistingLinks(SharedDataDirectory);
            LocalDataDirectory = ResolveExistingLinks(LocalDataDirectory);
            CacheDirectory = ResolveExistingLinks(CacheDirectory);
            LogsDirectory = ResolveExistingLinks(LogsDirectory);
            RuntimesDirectory = ResolveExistingLinks(RuntimesDirectory);
            InstancesDirectory = ResolveExistingLinks(InstancesDirectory);
            TempDirectory = ResolveExistingLinks(TempDirectory);
            ResourcesDirectory = ResolveExistingLinks(ResourcesDirectory);
        }
        LockFile = Path.Combine(LocalDataDirectory, "instance.lock");
        if (IsIsolated)
            foreach (var path in new[] { SharedDataDirectory, LocalDataDirectory, CacheDirectory, LogsDirectory,
                RuntimesDirectory, InstancesDirectory, TempDirectory })
                EnsureContained(DataDirectory, path);
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute directory is required; paths cannot depend on the current directory.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    // Use the identity of existing symlink ancestors, including when the final directory is new.
    internal static string ResolveExistingLinks(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(true)?.FullName
                    ?? throw new IOException("The data directory contains an unresolved symbolic link.");
        }
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return OperatingSystem.IsMacOS() ? MacFileSystemPaths.CanonicalDirectory(normalized) : normalized;
    }

    internal static void EnsureContained(string root, string path)
    {
        if (!JavaPathBoundary.ContainsCanonical(ResolveExistingLinks(root), ResolveExistingLinks(path)))
            throw new IOException("An isolated application path escapes its data root through a symbolic link.");
    }
}
