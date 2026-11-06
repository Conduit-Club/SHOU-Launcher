using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace SHOU.Launcher.Desktop.Views;

/// <summary>Renders the licensed, bundled Lucide assets in their original 24-unit viewport.</summary>
public sealed class ClubIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ClubIcon, string>(nameof(Kind), "play");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<ClubIcon>();
    private static readonly Dictionary<string, Geometry[]> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Names = ["play", "download", "settings", "wrench", "shirt", "pencil", "arrow-right-left", "refresh-cw"];
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static ClubIcon() => AffectsRender<ClubIcon>(KindProperty, ForegroundProperty);
    public ClubIcon() { Width = 20; Height = 20; IsHitTestVisible = false; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!Names.Contains(Kind)) return;
        if (!Cache.TryGetValue(Kind, out var parts))
        {
            using var source = AssetLoader.Open(new Uri($"avares://SHOU.Launcher.Desktop/Assets/Icons/Lucide/{Kind}.svg"));
            parts = XDocument.Load(source).Root!.Elements().Select(Parse).ToArray();
            Cache[Kind] = parts;
        }
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2));
        var pen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var part in parts) context.DrawGeometry(null, pen, part);
    }

    private static Geometry Parse(XElement element)
    {
        double Number(string key) => double.Parse(element.Attribute(key)!.Value, CultureInfo.InvariantCulture);
        return element.Name.LocalName switch
        {
            "path" => Geometry.Parse(element.Attribute("d")!.Value),
            "circle" => new EllipseGeometry(new Rect(Number("cx") - Number("r"), Number("cy") - Number("r"), 2 * Number("r"), 2 * Number("r"))),
            "line" => new LineGeometry(new Point(Number("x1"), Number("y1")), new Point(Number("x2"), Number("y2"))),
            "polyline" => Geometry.Parse("M " + element.Attribute("points")!.Value),
            _ => throw new InvalidDataException("Unsupported bundled icon primitive.")
        };
    }
}
