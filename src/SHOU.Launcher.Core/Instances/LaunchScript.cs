using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

public static class LaunchScript
{
    public static string Build(LauncherOS os, string executable, string dataDirectory, string instanceId, bool explicitDataDirectory = true)
    {
        SafePath.Identifier(instanceId);
        if (os is not (LauncherOS.Windows or LauncherOS.MacOS) || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(dataDirectory) || new[] { executable, dataDirectory }.Any(s => s.Any(char.IsControl))) throw new ArgumentException("启动脚本路径或平台无效。");
        // Route through the launcher; credentials, Java argv and user hooks are never exported.
        if (os == LauncherOS.Windows)
        {
            string Q(string value)
            {
                if (value.Contains('"')) throw new ArgumentException("Windows 脚本路径包含无效字符。");
                var trailing = value.Length - value.TrimEnd('\\').Length;
                return "\"" + value.Replace("%", "%%", StringComparison.Ordinal) + new string('\\', trailing) + "\"";
            }
            return "@echo off\r\nsetlocal DisableDelayedExpansion\r\nchcp 65001 >nul\r\n" + Q(executable) + (explicitDataDirectory ? " --data-dir " + Q(dataDirectory) : "") + " --launch-instance " + Q(instanceId) + "\r\nexit /b %errorlevel%\r\n";
        }
        static string S(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        return "#!/bin/sh\nexec " + S(executable) + (explicitDataDirectory ? " --data-dir " + S(dataDirectory) : "") + " --launch-instance " + S(instanceId) + "\n";
    }
    public static async Task SaveAsync(string destination, string content, bool overwrite, bool unixExecutable, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("请选择完整的脚本保存路径。");
        SafePath.RejectLinks(destination); SafePath.RejectLinks(destination + ".bak");
        byte[]? previous = null;
        if (File.Exists(destination))
        {
            if (!overwrite) throw new IOException("脚本已存在，请确认覆盖或另选文件名。");
            if (new FileInfo(destination).Length > 1024 * 1024) throw new IOException("目标文件过大，请另选文件名。");
            previous = await File.ReadAllBytesAsync(destination, ct);
        }
        var stage = destination + ".tmp-" + Guid.NewGuid().ToString("N"); var backupStage = stage + ".bak";
        try
        {
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) { await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct); await stream.FlushAsync(ct); }
            if (unixExecutable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(destination);
            var current = File.Exists(destination) ? await Content.WorldContentArchive.ReadFileAsync(Path.GetDirectoryName(destination)!, destination, 1024 * 1024, ct) : null;
            if (previous is null ? current is not null : current is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(previous), SHA256.HashData(current))) throw new IOException("目标脚本已变化，已保留其它程序的修改。");
            if (previous is not null)
            {
                await File.WriteAllBytesAsync(backupStage, previous, ct); ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(destination + ".bak"); File.Move(backupStage, destination + ".bak", true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(stage, destination, overwrite);
        }
        finally { if (File.Exists(stage)) { SafePath.RejectLinks(stage); File.Delete(stage); } if (File.Exists(backupStage)) { SafePath.RejectLinks(backupStage); File.Delete(backupStage); } }
    }
}
