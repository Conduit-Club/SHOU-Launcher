using System.Runtime.InteropServices;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class NativePlatformInfo : IPlatformInfo
{
    public PlatformContext Context { get; }

    public NativePlatformInfo()
    {
        var os = OperatingSystem.IsWindows() ? LauncherOS.Windows
            : OperatingSystem.IsMacOS() ? LauncherOS.MacOS
            : OperatingSystem.IsLinux() ? LauncherOS.Linux : LauncherOS.Unknown;
        var hardware = Normalize(RuntimeInformation.OSArchitecture);
        if (OperatingSystem.IsMacOS())
        {
            // This remains the hardware architecture when the launcher runs under Rosetta.
            nuint length = sizeof(int);
            if (SysctlByName("hw.optional.arm64", out var arm64, ref length, IntPtr.Zero, 0) == 0 && arm64 == 1)
                hardware = RuntimeArchitecture.Arm64;
        }
        Context = new PlatformContext(os, hardware, Normalize(RuntimeInformation.ProcessArchitecture),
            Environment.OSVersion.Version.ToString());
    }

    private static RuntimeArchitecture Normalize(Architecture architecture) => architecture switch
    {
        Architecture.X86 => RuntimeArchitecture.X86,
        Architecture.X64 => RuntimeArchitecture.X64,
        Architecture.Arm64 => RuntimeArchitecture.Arm64,
        _ => RuntimeArchitecture.Unknown
    };

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out int value, ref nuint length, IntPtr newValue, nuint newLength);
}
