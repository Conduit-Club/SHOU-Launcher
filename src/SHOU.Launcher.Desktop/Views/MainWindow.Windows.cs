using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using System.Runtime.InteropServices;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private void ConfigureWindowsChrome()
    {
        // Keep the native resize frame, work-area maximization and system menu while
        // using shared title-bar content. No Windows hooks enter a shared page VM.
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        Win32Properties.AddWindowStylesCallback(this, (style, extended) => (style | 0x00080000u, extended)); // WS_SYSMENU
        Win32Properties.AddWndProcHookCallback(this, WindowsCaptionMessage);
        WindowDecorationProperties.SetElementRole(this.FindControl<Border>("LauncherTitleBar")!, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(this.FindControl<StackPanel>("PrimaryNavigation")!, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(this.FindControl<Button>("BackNavigationButton")!, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(this.FindControl<Button>("MinimizeWindowButton")!, WindowDecorationsElementRole.MinimizeButton);
        WindowDecorationProperties.SetElementRole(this.FindControl<Button>("MaximizeWindowButton")!, WindowDecorationsElementRole.MaximizeButton);
        WindowDecorationProperties.SetElementRole(this.FindControl<Button>("CloseWindowButton")!, WindowDecorationsElementRole.CloseButton);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty || change.Property == CanMaximizeProperty) UpdateWindowsCaption();
            if (change.Property == OffScreenMarginProperty) this.FindControl<Border>("WindowSurface")!.Margin = OffScreenMargin;
        };
        UpdateWindowsCaption();
    }

    private IntPtr WindowsCaptionMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // BorderOnly keeps resizing/snap but DefWindowProc does not expose the
        // caption menu on every Windows version. Use that same native system menu.
        var keyboard = message == 0x0104 && wParam.ToInt64() == 0x20; // WM_SYSKEYDOWN / VK_SPACE
        var rightClick = message == 0x00A5 && wParam.ToInt64() == 2; // WM_NCRBUTTONUP / HTCAPTION
        if (!keyboard && !rightClick) return IntPtr.Zero;
        handled = true;
        var menu = WindowsCaptionMenu.GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero) return IntPtr.Zero;
        var point = keyboard ? this.PointToScreen(new Point(0, 48))
            : new PixelPoint((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
        var command = WindowsCaptionMenu.TrackPopupMenuEx(menu, 0x0102, point.X, point.Y, hwnd, IntPtr.Zero);
        if (command != 0) WindowsCaptionMenu.PostMessage(hwnd, 0x0112, (IntPtr)command, IntPtr.Zero); // WM_SYSCOMMAND
        WindowsCaptionMenu.PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        return IntPtr.Zero;
    }

    private static class WindowsCaptionMenu
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetSystemMenu(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);
        [DllImport("user32.dll")] internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }

    private void UpdateWindowsCaption()
    {
        var maximized = WindowState == WindowState.Maximized;
        var button = this.FindControl<Button>("MaximizeWindowButton")!;
        button.IsEnabled = CanMaximize;
        var label = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        this.FindControl<Avalonia.Controls.Shapes.Path>("MaximizeGlyph")!.Data = Geometry.Parse(maximized
            ? "M 3,3 H 11 V 11 H 3 Z M 5,3 V 1 H 13 V 9 H 11" : "M 1,1 H 11 V 11 H 1 Z");
        this.FindControl<Border>("WindowSurface")!.CornerRadius = new CornerRadius(maximized ? 0 : 6);
    }
}
