using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Views;

/// <summary>Software-projected Minecraft cuboids with per-texel UVs. Uses only validated image pixels and Avalonia drawing.</summary>
public sealed class AccountSkinPreviewControl : Control
{
    public static readonly StyledProperty<AccountTextureImage?> SkinProperty = AvaloniaProperty.Register<AccountSkinPreviewControl, AccountTextureImage?>(nameof(Skin));
    public static readonly StyledProperty<AccountTextureImage?> CapeProperty = AvaloniaProperty.Register<AccountSkinPreviewControl, AccountTextureImage?>(nameof(Cape));
    public static readonly StyledProperty<AccountSkinModel> ModelProperty = AvaloniaProperty.Register<AccountSkinPreviewControl, AccountSkinModel>(nameof(Model));
    public static readonly StyledProperty<bool> ShowOuterLayerProperty = AvaloniaProperty.Register<AccountSkinPreviewControl, bool>(nameof(ShowOuterLayer), true);
    public static readonly StyledProperty<bool> ShowCapeProperty = AvaloniaProperty.Register<AccountSkinPreviewControl, bool>(nameof(ShowCape), true);
    public AccountTextureImage? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public AccountTextureImage? Cape { get => GetValue(CapeProperty); set => SetValue(CapeProperty, value); }
    public AccountSkinModel Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool ShowOuterLayer { get => GetValue(ShowOuterLayerProperty); set => SetValue(ShowOuterLayerProperty, value); }
    public bool ShowCape { get => GetValue(ShowCapeProperty); set => SetValue(ShowCapeProperty, value); }
    public double Yaw { get; private set; } = -0.35;
    public double Pitch { get; private set; } = -0.08;
    public double Zoom { get; private set; } = 1;
    private Point? dragOrigin;
    private readonly List<Texel> mesh = [];
    private readonly Dictionary<uint, IBrush> brushes = [];
    static AccountSkinPreviewControl() => AffectsRender<AccountSkinPreviewControl>(SkinProperty, CapeProperty, ModelProperty, ShowOuterLayerProperty, ShowCapeProperty);
    public AccountSkinPreviewControl() { Focusable = true; ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Hand); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SkinProperty || change.Property == CapeProperty || change.Property == ModelProperty || change.Property == ShowOuterLayerProperty || change.Property == ShowCapeProperty) RebuildMesh();
    }
    public void ResetView() { Yaw = -0.35; Pitch = -0.08; Zoom = 1; InvalidateVisual(); }
    public void Rotate(double yaw, double pitch) { if (!double.IsFinite(yaw) || !double.IsFinite(pitch)) return; Yaw = Math.IEEERemainder(Yaw + yaw, Math.PI * 2); Pitch = Math.Clamp(Pitch + pitch, -1.1, 1.1); InvalidateVisual(); }
    public void ChangeZoom(double factor) { if (double.IsFinite(factor) && factor > 0) { Zoom = Math.Clamp(Zoom * factor, .55, 2.1); InvalidateVisual(); } }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { base.OnPointerPressed(e); if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { dragOrigin = e.GetPosition(this); e.Pointer.Capture(this); Focus(); e.Handled = true; } }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (dragOrigin is { } origin) { var next = e.GetPosition(this); Rotate((next.X - origin.X) * .012, (next.Y - origin.Y) * .008); dragOrigin = next; e.Handled = true; } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); dragOrigin = null; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); dragOrigin = null; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); ChangeZoom(Math.Exp(e.Delta.Y * .09)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key) { case Key.Left: Rotate(-.15, 0); break; case Key.Right: Rotate(.15, 0); break; case Key.Up: Rotate(0, -.1); break; case Key.Down: Rotate(0, .1); break; case Key.OemPlus: case Key.Add: ChangeZoom(1.1); break; case Key.OemMinus: case Key.Subtract: ChangeZoom(1 / 1.1); break; case Key.Home: ResetView(); break; default: return; }
        e.Handled = true;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        foreach (var face in Project(Bounds.Size))
        {
            var key = face.Color.ToUInt32(); if (!brushes.TryGetValue(key, out var brush)) brushes[key] = brush = new SolidColorBrush(face.Color);
            var geometry = new StreamGeometry(); using (var writer = geometry.Open()) { writer.BeginFigure(face.Points[0], true); for (var i = 1; i < 4; i++) writer.LineTo(face.Points[i]); writer.EndFigure(true); }
            context.DrawGeometry(brush, null, geometry);
        }
    }
    /// <summary>Returns the same visible UV texels submitted to the drawing context, far to near.</summary>
    public IReadOnlyList<AccountSkinProjectedTexel> Project(Size viewport)
    {
        var projected = new List<AccountSkinProjectedTexel>(mesh.Count / 2); var scale = Math.Min(viewport.Width / 25, viewport.Height / 38) * Zoom;
        if (scale <= 0) return projected;
        foreach (var face in mesh)
        {
            var normal = Turn(face.Normal, false); if (normal.Z >= -.0001) continue;
            var corners = face.Vertices.Select(v => Turn(v, true)).ToArray(); var light = Math.Clamp(.8 + -.16 * normal.Z + .08 * normal.Y - .04 * normal.X, .6, 1);
            var color = Color.FromArgb(face.Color.A, (byte)(face.Color.R * light), (byte)(face.Color.G * light), (byte)(face.Color.B * light));
            projected.Add(new(corners.Select(p => new Point(viewport.Width / 2 + p.X * scale, viewport.Height / 2 - p.Y * scale)).ToArray(), color, corners.Average(c => c.Z), face.Part));
        }
        projected.Sort((a, b) => b.Depth.CompareTo(a.Depth)); return projected;
    }
    private V Turn(V value, bool position)
    {
        var y = value.Y - (position ? 16 : 0); var x = value.X * Math.Cos(Yaw) + value.Z * Math.Sin(Yaw); var z = -value.X * Math.Sin(Yaw) + value.Z * Math.Cos(Yaw);
        return new(x, y * Math.Cos(Pitch) - z * Math.Sin(Pitch), y * Math.Sin(Pitch) + z * Math.Cos(Pitch));
    }
    private void RebuildMesh()
    {
        mesh.Clear(); brushes.Clear(); if (Skin is not { } skin) return; var slim = Model == AccountSkinModel.Slim; var arm = slim ? 3 : 4; var modern = skin.Height == 64;
        Box(skin, "head", -4, 24, -4, 8, 8, 8, 0, 0); Box(skin, "body", -4, 12, -2, 8, 12, 4, 16, 16);
        Box(skin, "rightArm", -4 - arm, 12, -2, arm, 12, 4, 40, 16); Box(skin, "leftArm", 4, 12, -2, arm, 12, 4, modern ? 32 : 40, modern ? 48 : 16, mirror: !modern);
        Box(skin, "rightLeg", -4, 0, -2, 4, 12, 4, 0, 16); Box(skin, "leftLeg", 0, 0, -2, 4, 12, 4, modern ? 16 : 0, modern ? 48 : 16, mirror: !modern);
        if (ShowOuterLayer)
        {
            Box(skin, "hat", -4, 24, -4, 8, 8, 8, 32, 0, .5);
            if (modern)
            {
                Box(skin, "jacket", -4, 12, -2, 8, 12, 4, 16, 32, .25);
                Box(skin, "rightSleeve", -4 - arm, 12, -2, arm, 12, 4, 40, 32, .25); Box(skin, "leftSleeve", 4, 12, -2, arm, 12, 4, 48, 48, .25);
                Box(skin, "rightTrouser", -4, 0, -2, 4, 12, 4, 0, 32, .25); Box(skin, "leftTrouser", 0, 0, -2, 4, 12, 4, 0, 48, .25);
            }
        }
        if (ShowCape && Cape is { } cape) Box(cape, "cape", -5, 7.8, 2.5, 10, 16, 1, 0, 0, cape: true);
        InvalidateVisual();
    }
    private void Box(AccountTextureImage texture, string part, double x, double y, double z, int w, int h, int d, int u, int v, double expand = 0, bool mirror = false, bool cape = false)
    {
        var x0 = x - expand; var y0 = y - expand; var z0 = z - expand; var x1 = x + w + expand; var y1 = y + h + expand; var z1 = z + d + expand;
        Face(new(x0, y1, z0), new(x1, y1, z0), new(x0, y0, z0), new(0, 0, -1), cape ? u + d + w + d : u + d, v + d, w, h);
        Face(new(x1, y1, z1), new(x0, y1, z1), new(x1, y0, z1), new(0, 0, 1), cape ? u + d : u + d + w + d, v + d, w, h);
        Face(new(x0, y1, z1), new(x0, y1, z0), new(x0, y0, z1), new(-1, 0, 0), mirror ? u + d + w : u, v + d, d, h);
        Face(new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z0), new(1, 0, 0), mirror ? u : u + d + w, v + d, d, h);
        Face(new(x0, y1, z1), new(x1, y1, z1), new(x0, y1, z0), new(0, 1, 0), u + d, v, w, d);
        Face(new(x0, y0, z0), new(x1, y0, z0), new(x0, y0, z1), new(0, -1, 0), u + d + w, v, w, d);
        void Face(V origin, V right, V down, V normal, int tx, int ty, int tw, int th)
        {
            var sourceScale = texture.Width / 64;
            for (var row = 0; row < th; row++) for (var column = 0; column < tw; column++)
            {
                var px = (tx + (mirror ? tw - column - 1 : column)) * sourceScale; var py = (ty + row) * sourceScale;
                if (px < 0 || py < 0 || px >= texture.Width || py >= texture.Height) continue;
                var rgba = texture.Rgba.Span.Slice((py * texture.Width + px) * 4, 4); if (rgba[3] == 0) continue;
                var color = Color.FromArgb(rgba[3], rgba[0], rgba[1], rgba[2]);
                var a = At((double)column / tw, (double)row / th); var b = At((double)(column + 1) / tw, (double)row / th); var c = At((double)(column + 1) / tw, (double)(row + 1) / th); var e = At((double)column / tw, (double)(row + 1) / th);
                mesh.Add(new([a, b, c, e], normal, color, part));
            }
            V At(double across, double below)
            { var point = origin + (right - origin) * across + (down - origin) * below; return cape ? point with { Z = point.Z + (24 - point.Y) * .12 } : point; }
        }
    }
    private readonly record struct V(double X, double Y, double Z)
    { public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z); public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z); public static V operator *(V a, double b) => new(a.X * b, a.Y * b, a.Z * b); }
    private sealed record Texel(V[] Vertices, V Normal, Color Color, string Part);
}
public sealed record AccountSkinProjectedTexel(Point[] Points, Color Color, double Depth, string Part);

public sealed class AccountSkinAvatarControl : Control
{
    public static readonly StyledProperty<AccountTextureImage?> SkinProperty = AvaloniaProperty.Register<AccountSkinAvatarControl, AccountTextureImage?>(nameof(Skin));
    public AccountTextureImage? Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    static AccountSkinAvatarControl() => AffectsRender<AccountSkinAvatarControl>(SkinProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context); var size = Math.Min(Bounds.Width, Bounds.Height); var left = (Bounds.Width - size) / 2; var top = (Bounds.Height - size) / 2;
        if (Skin is not { } image)
        { var brush = new SolidColorBrush(Color.FromArgb(100, 146, 123, 139)); context.DrawRectangle(null, new Pen(brush, 1.5), new Rect(left + size * .2, top + size * .12, size * .6, size * .62), 2, 2); context.DrawLine(new Pen(brush, 1.5), new(left + size * .14, top + size * .9), new(left + size * .86, top + size * .9)); return; }
        Draw(8, 8); Draw(40, 8);
        void Draw(int ox, int oy)
        {
            for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
            { var bytes = image.Rgba.Span.Slice(((oy + y) * image.Width + ox + x) * 4, 4); if (bytes[3] != 0) context.FillRectangle(new SolidColorBrush(Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])), new Rect(left + x * size / 8, top + y * size / 8, size / 8 + .02, size / 8 + .02)); }
        }
    }
}
