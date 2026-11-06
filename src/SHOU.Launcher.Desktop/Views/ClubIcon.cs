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
    public static IReadOnlySet<string> AvailableKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "anvil", "arrow-left", "arrow-right-left", "arrow-right", "arrow-up-down", "arrow-up-from-line", "arrow-up-to-line",
        "book-marked", "box", "boxes", "bubbles", "cat", "chevron-left", "chevron-right", "chevrons-left",
        "circle-check", "circle-help", "circle-minus", "circle-plus", "coffee", "copy", "download", "earth", "egg",
        "ethernet-port", "external-link", "file-archive", "file-box", "file-input", "flag", "flask-conical", "folder-input",
        "folder-open", "folder-plus", "gauge", "git-pull-request-arrow", "globe", "grid-2x2", "hammer", "heart-filled",
        "heart-off", "heart", "image", "info", "layers", "layout-dashboard", "layout-list", "link-2-off", "link-2",
        "list-x", "list", "log-out", "message-circle", "minus", "monitor-cog", "move-down", "move-up", "music", "network",
        "package-plus", "package", "palette", "pencil", "play", "power", "puzzle", "refresh-cw", "rocket", "rotate-ccw",
        "save", "scroll-text", "scroll", "search", "server", "settings", "share-2", "shield-check", "shirt", "sparkles",
        "tool-case", "trash-2", "triangle-alert", "upload", "user-plus", "user", "wrench", "x"
    };
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static ClubIcon() => AffectsRender<ClubIcon>(KindProperty, ForegroundProperty);
    public ClubIcon() { Width = 20; Height = 20; IsHitTestVisible = false; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var parts = GetShapes(Kind);
        if (parts.Count == 0) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2));
        var pen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var part in parts) context.DrawGeometry(null, pen, part);
    }

    public static IReadOnlyList<Geometry> GetShapes(string kind)
    {
        if (!AvailableKinds.Contains(kind)) return [];
        if (!Cache.TryGetValue(kind, out var parts))
        {
            using var source = AssetLoader.Open(new Uri($"avares://SHOU.Launcher.Desktop/Assets/Icons/Lucide/{kind}.svg"));
            parts = XDocument.Load(source).Root!.Elements().Select(Parse).ToArray();
            Cache[kind] = parts;
        }
        return parts;
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
            "polygon" => Geometry.Parse("M " + element.Attribute("points")!.Value + " Z"),
            "ellipse" => new EllipseGeometry(new Rect(Number("cx") - Number("rx"), Number("cy") - Number("ry"), 2 * Number("rx"), 2 * Number("ry"))),
            "rect" => new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")),
                element.Attribute("rx") is null ? 0 : Number("rx"), element.Attribute("ry") is not null ? Number("ry") : element.Attribute("rx") is null ? 0 : Number("rx")),
            _ => throw new InvalidDataException("Unsupported bundled icon primitive.")
        };
    }
}
