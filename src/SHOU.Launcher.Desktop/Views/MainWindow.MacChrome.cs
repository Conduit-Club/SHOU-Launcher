using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private IDisposable? macChromeRetry, macChromeTransitionRetry;
    private void InitializeMacTrafficLights()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // AppKit owns the buttons and their actions. Adjust their native titlebar layout only.
        Opened += (_, _) => QueueMacTrafficLights();
        SizeChanged += (_, _) => QueueMacTrafficLights();
        Activated += (_, _) => QueueMacTrafficLights();
        Deactivated += (_, _) => QueueMacTrafficLights();
        Closed += (_, _) => { macChromeRetry?.Dispose(); macChromeTransitionRetry?.Dispose(); };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) QueueMacTrafficLights(); };
    }
    private void QueueMacTrafficLights()
    {
        Dispatcher.UIThread.Post(AlignMacTrafficLights, DispatcherPriority.Render);
        macChromeRetry?.Dispose();
        macChromeRetry = DispatcherTimer.RunOnce(AlignMacTrafficLights, TimeSpan.FromMilliseconds(150));
        macChromeTransitionRetry?.Dispose();
        macChromeTransitionRetry = DispatcherTimer.RunOnce(AlignMacTrafficLights, TimeSpan.FromMilliseconds(1000));
    }
    private void AlignMacTrafficLights()
    {
        if (!OperatingSystem.IsMacOS() || !IsVisible || WindowState is WindowState.FullScreen or WindowState.Minimized ||
            TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle { NSWindow: not 0 } handle) return;
        var buttons = Enumerable.Range(0, 3).Select(i => NativeButton(handle.NSWindow, Selector("standardWindowButton:"), i)).ToArray();
        if (buttons.Any(b => b == 0)) return;
        var container = NativeObject(buttons[0], Selector("superview"));
        var sameParent = container != 0 && buttons.All(b => NativeObject(b, Selector("superview")) == container);
        Bootstrap.MacTitlebarObservation = new { sameParent, containerFrame = container == 0 ? null : RectValues(NativeFrame(container, Selector("frame"))), buttonFrames = buttons.Select(b => RectValues(NativeFrame(b, Selector("frame")))).ToArray() };
        Bootstrap.WriteNativeReport("native_titlebar_observed");
        if (!sameParent) return;
        var frame = NativeFrame(container, Selector("frame"));
        if (frame.Height < 18 || frame.Width < 60 || frame.Height > 96 && Math.Abs(frame.Height - Bounds.Height) > 64) return;
        const double height = 48;
        if (frame.Height <= 96 && Math.Abs(frame.Height - height) > .1)
        {
            var parent = NativeObject(container, Selector("superview"));
            if (parent == 0) return;
            var flipped = NativeBool(parent, Selector("isFlipped"));
            NativeSetFrame(container, Selector("setFrame:"), new(frame.X, flipped ? frame.Y : frame.Y + frame.Height - height, frame.Width, height));
        }
        foreach (var button in buttons)
        {
            var bounds = NativeFrame(button, Selector("frame"));
            if (bounds.Height is < 8 or > 24) continue;
            var parentHeight = frame.Height > 96 ? frame.Height : height;
            var y = NativeBool(container, Selector("isFlipped")) ? (height - bounds.Height) / 2 : parentHeight - (height + bounds.Height) / 2;
            NativeSetOrigin(button, Selector("setFrameOrigin:"), new(bounds.X, y));
        }
    }
    private static double[] RectValues(NativeRect frame) => [frame.X, frame.Y, frame.Width, frame.Height];
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativeRect(double x, double y, double width, double height)
    { public readonly double X = x, Y = y, Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativePoint(double x, double y)
    { public readonly double X = x, Y = y; }
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint NativeObject(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint NativeButton(nint receiver, nint selector, nint kind);
    private static NativeRect NativeFrame(nint receiver, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return NativeFrameArm64(receiver, selector);
        NativeFrameX64(out var frame, receiver, selector); return frame;
    }
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativeRect NativeFrameArm64(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void NativeFrameX64(out NativeRect frame, nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool NativeBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void NativeSetFrame(nint receiver, nint selector, NativeRect frame);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void NativeSetOrigin(nint receiver, nint selector, NativePoint origin);
}
