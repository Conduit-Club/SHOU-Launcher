using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SHOU.Launcher.Core.Tests;

/// <summary>Non-elevated native fixtures, restricted to the guarded task's owned test-data.</summary>
internal static class NativeWindowsLinks
{
    private static void Validate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var task = Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Guarded task required.");
        var owned = Path.GetFullPath(Path.Combine(task, "test-data")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(owned, StringComparison.OrdinalIgnoreCase)) throw new IOException("Native fixture outside owned test-data.");
    }

    public static void CreateHardLink(string link, string target)
    {
        Validate(link); Validate(target);
        if (!CreateHardLinkW(link, target, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static async Task CreateDirectoryJunctionAsync(string link, string target)
    {
        Validate(link); Validate(target);
        if (!Directory.Exists(target) || Directory.Exists(link) || File.Exists(link)) throw new IOException("Native fixture path is not new.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start owned junction provisioner.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0 || new DirectoryInfo(link).LinkTarget is null) throw new IOException("Native junction fixture could not be provisioned.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
}
