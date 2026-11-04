using System.Diagnostics;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class ExternalLauncher(IProcessRunner processes, PlatformContext context) : IExternalLauncher
{
    public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Only absolute HTTP(S)/mailto links without embedded credentials may be opened.");
        return OpenAsync(uri.AbsoluteUri, false, cancellationToken);
    }

    public Task OpenDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ValidatePath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The requested directory does not exist.");
        return OpenAsync(Path.GetFullPath(directory), false, cancellationToken);
    }

    public Task RevealFileAsync(string file, CancellationToken cancellationToken = default)
    {
        ValidatePath(file);
        if (!File.Exists(file)) throw new FileNotFoundException("The requested file does not exist.");
        return OpenAsync(Path.GetFullPath(file), true, cancellationToken);
    }

    private async Task OpenAsync(string target, bool reveal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.OS == LauncherOS.MacOS && OperatingSystem.IsMacOS())
        {
            var result = await processes.RunAsync(new ProcessSpec("/usr/bin/open", reveal ? ["-R", "--", target] : ["--", target], "/",
                Timeout: TimeSpan.FromSeconds(15), OutputLimitCharacters: 32768), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || result.TimedOut || result.Cancelled)
                throw new IOException("LaunchServices 未接受打开请求。");
        }
        else if (context.OS == LauncherOS.Windows && OperatingSystem.IsWindows())
        {
            var start = reveal
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false }
                : new ProcessStartInfo(target) { UseShellExecute = true };
            if (reveal) { start.ArgumentList.Add("/select,"); start.ArgumentList.Add(target); }
            using var process = Process.Start(start);
            // This acknowledges the OS opening request, not that another application's window is ready.
        }
        else throw new PlatformNotSupportedException("No native desktop opener is registered for this platform.");
    }

    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute path is required.");
    }
}
