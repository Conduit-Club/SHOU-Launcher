using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform.MacOS;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Desktop.Platform;

public static class PlatformServicesFactory
{
    public static PlatformServices Create(PlatformOptions options, IVerifiedDownloader downloader)
        => CreateCore(options, downloader, null);

    /// <summary>Compose services with an explicitly supplied credential adapter, including isolated test stores.</summary>
    public static PlatformServices Create(PlatformOptions options, IVerifiedDownloader downloader, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return CreateCore(options, downloader, credentials);
    }

    private static PlatformServices CreateCore(PlatformOptions options, IVerifiedDownloader downloader, ICredentialStore? credentials)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        var info = new NativePlatformInfo();
        var paths = new AppPaths(options, info.Context);
        var processes = new ProcessRunner();
        credentials ??= CreateNativeCredentials(paths);
        var discovery = new JavaDiscovery(processes, info.Context, options, managedRuntimeDirectory: paths.RuntimesDirectory);
        var installer = new MojangJavaRuntimeInstaller(downloader, discovery, paths.RuntimesDirectory,
            Path.Combine(paths.CacheDirectory, "java-metadata"));
        IJavaRuntimeInstaller runtimeInstaller = OperatingSystem.IsMacOS() ? new MacJava8RuntimeInstaller(installer, downloader, discovery, paths.RuntimesDirectory, paths.CacheDirectory) : installer;
        return new PlatformServices(info, paths, new Capabilities(info.Context), new ExternalLauncher(processes, info.Context),
            credentials, processes, discovery, runtimeInstaller, new DataDirectoryLock())
        {
            LegacyDesktopData = OperatingSystem.IsWindows() && !paths.IsIsolated
                ? new WindowsLegacyDesktopDataReader(paths.ResourcesDirectory, paths.SharedDataDirectory) : null,
            MemoryInfo = new NativeMemoryInfo(),
            LegacyMicrosoftAccounts = OperatingSystem.IsWindows()
                ? new WindowsLegacyMicrosoftAccountReader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCLCE")) : null
        };
    }

    private static ICredentialStore CreateNativeCredentials(IAppPaths paths)
    {
        if (OperatingSystem.IsMacOS()) return new KeychainCredentialStore(paths.CredentialScope);
        if (OperatingSystem.IsWindows()) return new WindowsDpapiCredentialStore(paths);
        throw new PlatformNotSupportedException("This Desktop host supports Windows and macOS; no credential adapter is registered for this OS.");
    }

    private sealed class Capabilities(PlatformContext context) : IPlatformCapabilities
    {
        public bool SupportsCredentialPersistence => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public bool SupportsSystemProxy => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public bool SupportsRevealFile => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public IReadOnlyList<string> Limitations => [];
    }
}
