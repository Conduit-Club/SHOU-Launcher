using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class NativeMemoryInfo : IMemoryInfo
{
    public MemoryStatus Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) throw new IOException("无法读取系统内存。");
            return new((long)status.TotalPhysical, (long)status.AvailablePhysical);
        }
        if (OperatingSystem.IsMacOS())
        {
            var total = long.Parse(ReadCommand("/usr/sbin/sysctl", "-n", "hw.memsize").Trim(), CultureInfo.InvariantCulture);
            var text = ReadCommand("/usr/bin/vm_stat");
            var pageSize = long.Parse(Regex.Match(text, @"page size of (\d+) bytes").Groups[1].Value, CultureInfo.InvariantCulture);
            long Pages(string key) => long.TryParse(Regex.Match(text, Regex.Escape(key) + @":\s+(\d+)").Groups[1].Value, out var pages) ? pages : 0;
            return new(total, Math.Min(total, (Pages("Pages free") + Pages("Pages inactive") + Pages("Pages speculative")) * pageSize));
        }
        throw new PlatformNotSupportedException("该平台尚无系统内存适配器。");
    }
    private static string ReadCommand(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法读取系统内存。");
        if (!process.WaitForExit(1000)) { process.Kill(); throw new IOException("读取系统内存超时。"); }
        if (process.ExitCode != 0) throw new IOException("无法读取系统内存。");
        return process.StandardOutput.ReadToEnd();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
