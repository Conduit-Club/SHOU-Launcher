using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private IDisposable? macChromeRetry, macChromeTransitionRetry;
    private DispatcherTimer? macChromeMaintenance;
    private void InitializeMacTrafficLights()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // AppKit owns the buttons and their actions. Adjust their native titlebar layout only.
        Opened += (_, _) =>
        {
            QueueMacTrafficLights();
            if (TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle) return;
            // AppKit can relayout standard buttons independently of Avalonia layout events.
            // Read native geometry; write only when its center has actually drifted.
            macChromeMaintenance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            macChromeMaintenance.Tick += (_, _) => AlignMacTrafficLights();
            macChromeMaintenance.Start();
        };
        SizeChanged += (_, _) => QueueMacTrafficLights();
        Activated += (_, _) => QueueMacTrafficLights();
        Deactivated += (_, _) => QueueMacTrafficLights();
        Closed += (_, _) => { macChromeRetry?.Dispose(); macChromeTransitionRetry?.Dispose(); macChromeMaintenance?.Stop(); };
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
        if (macChromeMaintenance is null) Bootstrap.WriteNativeReport("native_titlebar_observed");
        if (!sameParent) return;
        var frame = NativeFrame(container, Selector("frame"));
        if (frame.Height < 18 || frame.Width < 60 || frame.Height > 96 && Math.Abs(frame.Height - Bounds.Height) > 64) return;
        const double height = 48;
        for (var view = container; view != 0; view = NativeObject(view, Selector("superview")))
        {
            var box = NativeFrame(view, Selector("frame"));
            if (box.Height is < 18 or > 96 || box.Width < 60) break;
            if (Math.Abs(box.Height - height) < .1) continue;
            var parent = NativeObject(view, Selector("superview")); if (parent == 0) break;
            var flipped = NativeBool(parent, Selector("isFlipped"));
            NativeSetFrame(view, Selector("setFrame:"), new(box.X, flipped ? box.Y : box.Y + box.Height - height, box.Width, height));
        }
        var targetY = NativeFrame(handle.NSWindow, Selector("frame")).Height - height / 2;
        foreach (var button in buttons)
        {
            var bounds = NativeFrame(button, Selector("frame"));
            if (bounds.Height is < 8 or > 24) continue;
            var local = NativeFrame(button, Selector("bounds"));
            var center = NativeConvertPoint(button, Selector("convertPoint:toView:"), new(local.X + local.Width / 2, local.Y + local.Height / 2), 0);
            var delta = targetY - center.Y;
            if (Math.Abs(delta) < .25) continue;
            NativeSetOrigin(button, Selector("setFrameOrigin:"), new(bounds.X, bounds.Y + (NativeBool(container, Selector("isFlipped")) ? -delta : delta)));
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
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NativePoint NativeConvertPoint(nint receiver, nint selector, NativePoint point, nint view);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void NativeSetOrigin(nint receiver, nint selector, NativePoint origin);
}
