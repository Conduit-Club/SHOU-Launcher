using System.Runtime.InteropServices;
using SHOU.Launcher.Core.Tools;

namespace SHOU.Launcher.Desktop.Platform;

/// <summary>Launcher shortcuts only. The target is our current apphost, never a game or arbitrary command.</summary>
public static class LauncherShortcut
{
    public static string Extension => OperatingSystem.IsMacOS() ? "app" : "lnk";
    public static async Task CreateAsync(string destination, string dataDirectory, CancellationToken ct = default)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("找不到当前启动器。");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("请在打包后的启动器中创建快捷方式。");
        destination = ToolFileTransfer.ValidateDestination(destination);
        if (!Path.GetExtension(destination).Equals("." + Extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("快捷方式扩展名无效。");
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) await CreateWindowsAsync(destination, executable, dataDirectory, ct);
        else if (OperatingSystem.IsMacOS()) await CreateMacAsync(destination, executable, dataDirectory, ct);
        else throw new PlatformNotSupportedException("当前系统没有注册快捷方式适配器。");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Task CreateWindowsAsync(string destination, string executable, string dataDirectory, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null, shortcut = null; var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".shou-shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
            Exception? failure = null;
            try
            {
                ct.ThrowIfCancellationRequested(); var type = Type.GetTypeFromProgID("WScript.Shell", true)!;
                shell = Activator.CreateInstance(type)!; dynamic s = shell; shortcut = s.CreateShortcut(stage); dynamic link = shortcut;
                link.TargetPath = executable; link.Arguments = "--data-dir " + QuoteWindows(dataDirectory);
                link.WorkingDirectory = Path.GetDirectoryName(executable); link.Description = "潮涌核心社启动器"; link.IconLocation = executable + ",0"; link.Save();
                ct.ThrowIfCancellationRequested(); ToolFileTransfer.ValidateDestination(destination); File.Move(stage, destination, false);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                    if (File.Exists(stage)) File.Delete(stage);
                }
                catch (Exception ex) { failure ??= ex; }
            }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }) { IsBackground = true, Name = "SHOU shortcut writer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static string QuoteWindows(string argument)
    {
        var result = new System.Text.StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task CreateMacAsync(string destination, string executable, string dataDirectory, CancellationToken ct)
    {
        var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".shou-shortcut-" + Guid.NewGuid().ToString("N") + ".app");
        var macos = Path.Combine(stage, "Contents", "MacOS"); Directory.CreateDirectory(macos);
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        // Generated code has only literal, shell-quoted paths; this script does not interpolate user commands.
        var runner = Path.Combine(macos, "launch");
        var plist = Path.Combine(stage, "Contents", "Info.plist");
        try
        {
            await File.WriteAllTextAsync(runner, "#!/bin/sh\nexec " + Quote(executable) + " --data-dir " + Quote(dataDirectory) + "\n", ct);
            File.SetUnixFileMode(runner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(plist, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>launch</string><key>CFBundleIdentifier</key><string>club.conduit.shou.shortcut</string><key>CFBundleName</key><string>潮涌核心社启动器</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>", ct);
            ct.ThrowIfCancellationRequested(); ToolFileTransfer.ValidateDestination(destination); Directory.Move(stage, destination);
        }
        finally
        {
            // Only these two generated files are owned; never recursively remove a directory.
            if (Directory.Exists(stage))
            {
                if (File.Exists(runner)) File.Delete(runner); if (File.Exists(plist)) File.Delete(plist);
                if (!Directory.EnumerateFileSystemEntries(macos).Any()) Directory.Delete(macos);
                var contents = Path.Combine(stage, "Contents"); if (!Directory.EnumerateFileSystemEntries(contents).Any()) Directory.Delete(contents);
                if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
            }
        }
    }
}
