using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SHOU.Launcher.Desktop.Views;

/// <summary>A shared content-area loading illustration; no window layout changes.</summary>
public sealed class LoadingGlyph : UserControl
{
    private readonly ClubIcon icon = new() { Kind = "pickaxe", Width = 42, Height = 42, RenderTransformOrigin = new(.5, .5, RelativeUnit.Relative) };
    private readonly RotateTransform rotation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private int frame;
    public LoadingGlyph()
    {
        Width = Height = 48; icon.RenderTransform = rotation;
        Content = new Viewbox { Child = icon, Stretch = Stretch.Uniform };
        timer.Tick += (_, _) => { if (IsEffectivelyVisible) rotation.Angle = Math.Sin(++frame * .35) * 22; };
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }
}
