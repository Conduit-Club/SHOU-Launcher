using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Desktop.Platform.MacOS;
using SHOU.Launcher.Desktop.Platform.Windows;

namespace SHOU.Launcher.Desktop.Platform;

public static class PlatformServicesFactory
{
    public static PlatformServices Create(PlatformOptions options, IVerifiedDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        var info = new NativePlatformInfo();
        var paths = new AppPaths(options, info.Context);
        var processes = new ProcessRunner();
        ICredentialStore credentials;
        if (OperatingSystem.IsMacOS()) credentials = new KeychainCredentialStore(paths.CredentialScope);
        else if (OperatingSystem.IsWindows()) credentials = new WindowsDpapiCredentialStore(paths);
        else throw new PlatformNotSupportedException("This Desktop host supports Windows and macOS; no credential adapter is registered for this OS.");
        var discovery = new JavaDiscovery(processes, info.Context, options, managedRuntimeDirectory: paths.RuntimesDirectory);
        var installer = new MojangJavaRuntimeInstaller(downloader, discovery, paths.RuntimesDirectory,
            Path.Combine(paths.CacheDirectory, "java-metadata"));
        return new PlatformServices(info, paths, new Capabilities(info.Context), new ExternalLauncher(processes, info.Context),
            credentials, processes, discovery, installer, new DataDirectoryLock());
    }

    private sealed class Capabilities(PlatformContext context) : IPlatformCapabilities
    {
        public bool SupportsCredentialPersistence => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public bool SupportsSystemProxy => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public bool SupportsRevealFile => context.OS is LauncherOS.MacOS or LauncherOS.Windows;
        public IReadOnlyList<string> Limitations => context.OS == LauncherOS.Windows
            ? ["旧 Windows 账户与历史字段的自动接管尚未完成；现有 UserKey.bin 和账户文件保持原状。"] : [];
    }
}
