using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;

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
