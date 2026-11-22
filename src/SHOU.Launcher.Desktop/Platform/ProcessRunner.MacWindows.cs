using System.Diagnostics;
using System.Runtime.InteropServices;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public sealed partial class ProcessRunner
{
    private static async Task<bool> WaitForMacGameWindowAsync(IManagedProcess process, TimeSpan timeout, CancellationToken ct)
    {
        using var windows = new MacGameWindowQuery();
        var timer = Stopwatch.StartNew();
        while ((timeout == Timeout.InfiniteTimeSpan || timer.Elapsed < timeout) && !process.Completion.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            if (windows.HasWindow(process.ProcessId)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }
}

/// <summary>Window-server metadata only: no screenshots, window titles, Accessibility or permission prompts.</summary>
internal sealed class MacGameWindowQuery : IDisposable
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private readonly List<nint> keys = [];

    public MacGameWindowQuery()
    {
        try
        {
            foreach (var key in new[] { "kCGWindowOwnerPID", "kCGWindowLayer", "kCGWindowBounds", "Width", "Height", "kCGWindowAlpha" })
            {
                var value = CFStringCreateWithCString(0, key, 0x08000100);
                if (value == 0) throw new IOException("无法创建窗口元数据查询。");
                keys.Add(value);
            }
        }
        catch { Dispose(); throw; }
    }

    public bool HasWindow(int processId)
    {
        // On-screen windows only, excluding desktop elements. Owner PID and bounds are public metadata;
        // deliberately do not request kCGWindowName or any window pixel content.
        var list = CGWindowListCopyWindowInfo(1 | 16, 0);
        if (list == 0) throw new IOException("当前 Mac 窗口列表不可用。");
        try
        {
            var count = CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var window = CFArrayGetValueAtIndex(list, i);
                if (!IsDictionary(window) || !ReadNumber(window, keys[0], out var owner) || owner != processId) continue;
                var bounds = CFDictionaryGetValue(window, keys[2]);
                if (IsDictionary(bounds) && ReadNumber(window, keys[1], out var layer) && ReadNumber(bounds, keys[3], out var width)
                    && ReadNumber(bounds, keys[4], out var height) && ReadNumber(window, keys[5], out var alpha)
                    && IsGameWindow(processId, owner, layer, width, height, alpha)) return true;
            }
            return false;
        }
        finally { CFRelease(list); }
    }

    // Excludes Java's hidden/tiny AWT helper windows and unrelated PIDs; this is not a user window-size setting.
    internal static bool IsGameWindow(int targetPid, double ownerPid, double layer, double width, double height, double alpha)
        => targetPid > 0 && ownerPid == targetPid && layer == 0 && double.IsFinite(width) && double.IsFinite(height)
            && width >= 64 && height >= 64 && double.IsFinite(alpha) && alpha > 0;
    private static bool IsDictionary(nint value) => value != 0 && CFGetTypeID(value) == CFDictionaryGetTypeID();
    private static bool ReadNumber(nint dictionary, nint key, out double result)
    {
        result = 0;
        var value = CFDictionaryGetValue(dictionary, key);
        return value != 0 && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, 13, out result);
    }
    public void Dispose() { foreach (var key in keys) CFRelease(key); keys.Clear(); }

    [DllImport(CoreGraphics)] private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFDictionaryGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint value, int numberType, out double result);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
