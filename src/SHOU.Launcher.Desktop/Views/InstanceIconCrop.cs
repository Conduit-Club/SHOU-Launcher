using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SkiaSharp;

namespace SHOU.Launcher.Desktop.Views;

public sealed class InstanceIconCropModel : ObservableObject, IDisposable
{
    private InstanceIconPixels source; private double zoom = 1, x = .5, y = .5; private Bitmap? preview;
    public double Zoom { get => zoom; set { if (SetProperty(ref zoom, Math.Clamp(value, 1, 8))) Refresh(); } }
    public double OffsetX { get => x; set { if (SetProperty(ref x, Math.Clamp(value, 0, 1))) Refresh(); } }
    public double OffsetY { get => y; set { if (SetProperty(ref y, Math.Clamp(value, 0, 1))) Refresh(); } }
    public Bitmap? Preview => preview;
    public double HorizontalOverflow => source.Width / (Math.Min(source.Width, source.Height) / Zoom) - 1;
    public double VerticalOverflow => source.Height / (Math.Min(source.Width, source.Height) / Zoom) - 1;
    public InstanceIconCropModel(InstanceIconPixels image) { source = image; Refresh(); }
    public byte[] Crop(CancellationToken ct = default) => InstanceIconImages.Crop(source, Zoom, OffsetX, OffsetY, ct);
    private void Refresh() { var bytes = Crop(); using var stream = new MemoryStream(bytes); var next = new Bitmap(stream); var old = preview; preview = next; OnPropertyChanged(nameof(Preview)); old?.Dispose(); }
    public void Reset() { zoom = 1; x = y = .5; OnPropertyChanged(nameof(Zoom)); OnPropertyChanged(nameof(OffsetX)); OnPropertyChanged(nameof(OffsetY)); Refresh(); }
    public void Rotate()
    {
        var pixels = new byte[source.Rgba.Length]; var original = source.Rgba.Span;
        for (var py = 0; py < source.Height; py++) for (var px = 0; px < source.Width; px++) original.Slice((py * source.Width + px) * 4, 4).CopyTo(pixels.AsSpan((px * source.Height + source.Height - py - 1) * 4, 4));
        source = new(source.Height, source.Width, pixels); Reset();
    }
    public static InstanceIconPixels ReadImage(string path, CancellationToken ct = default)
    {
        SHOU.Launcher.Core.IO.SafePath.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 16 * 1024 * 1024) throw new IOException("请选择不超过 16 MiB 的图片。");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); ct.ThrowIfCancellationRequested();
        using var codec = SKCodec.Create(new SKMemoryStream(bytes)) ?? throw new InvalidDataException("无法读取此图片。");
        var info = codec.Info;
        if (info.Width is < 1 or > 8192 || info.Height is < 1 or > 8192 || (long)info.Width * info.Height > 32 * 1024 * 1024 || codec.FrameCount > 1) throw new InvalidDataException("图片像素过大或为动画，请选择静态图片。");
        using var image = SKBitmap.Decode(codec) ?? throw new InvalidDataException("图片不完整或无法解码。");
        var scale = Math.Min(1d, 2048d / Math.Max(image.Width, image.Height)); var w = Math.Max(1, (int)(image.Width * scale)); var h = Math.Max(1, (int)(image.Height * scale)); var rgba = new byte[w * h * 4];
        for (var py = 0; py < h; py++) { ct.ThrowIfCancellationRequested(); for (var px = 0; px < w; px++) { var color = image.GetPixel(Math.Min(image.Width - 1, (int)(px / scale)), Math.Min(image.Height - 1, (int)(py / scale))); var i = (py * w + px) * 4; rgba[i] = color.Red; rgba[i + 1] = color.Green; rgba[i + 2] = color.Blue; rgba[i + 3] = color.Alpha; } }
        return new(w, h, rgba);
    }
    public void Dispose() { preview?.Dispose(); preview = null; }
}

public sealed class InstanceIconCropCanvas : Control
{
    public InstanceIconCropModel Model { get; }
    private Point? drag;
    public InstanceIconCropCanvas(InstanceIconCropModel model)
    {
        Model = model; Width = Height = 256; Focusable = true; Cursor = new(StandardCursorType.Hand);
        model.PropertyChanged += (_, _) => InvalidateVisual();
    }
    public override void Render(DrawingContext context) { context.FillRectangle(Brushes.LightGray, new Rect(Bounds.Size)); if (Model.Preview is { } image) context.DrawImage(image, new Rect(Bounds.Size)); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { drag = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; } }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); if (drag is not { } previous) return; var point = e.GetPosition(this); drag = point;
        if (Model.HorizontalOverflow > 0) Model.OffsetX -= (point.X - previous.X) / (Bounds.Width * Model.HorizontalOverflow);
        if (Model.VerticalOverflow > 0) Model.OffsetY -= (point.Y - previous.Y) / (Bounds.Height * Model.VerticalOverflow);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { drag = null; e.Pointer.Capture(null); base.OnPointerReleased(e); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { Model.Zoom = Math.Clamp(Model.Zoom * Math.Pow(1.15, e.Delta.Y), 1, 8); e.Handled = true; base.OnPointerWheelChanged(e); }
}

public sealed class InstanceIconCropWindow : Window
{
    public InstanceIconCropModel Model { get; }
    public InstanceIconCropWindow(InstanceIconPixels source, Func<byte[], Task<bool>> save, Action? cancelSaving = null, Func<string?>? error = null)
    {
        Title = "裁剪实例图标"; Width = 450; Height = 540; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Model = new(source); DataContext = Model;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        var canvas = new InstanceIconCropCanvas(Model) { Name = "InstanceIconCropCanvas", HorizontalAlignment = HorizontalAlignment.Center }; panel.Children.Add(canvas); var editors = new List<Control> { canvas };
        foreach (var row in new[] { ("缩放", nameof(Model.Zoom), 1d, 8d), ("水平位置", nameof(Model.OffsetX), 0d, 1d), ("垂直位置", nameof(Model.OffsetY), 0d, 1d) })
        {
            var grid = new Grid { ColumnDefinitions = new("80,*") }; grid.Children.Add(new TextBlock { Text = row.Item1, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Name = "Crop" + row.Item2, Minimum = row.Item3, Maximum = row.Item4 }; slider.Bind(Slider.ValueProperty, new Avalonia.Data.Binding(row.Item2) { Mode = Avalonia.Data.BindingMode.TwoWay }); Grid.SetColumn(slider, 1); grid.Children.Add(slider); panel.Children.Add(grid); editors.Add(grid);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        var rotate = new Button { Content = "旋转", Name = "RotateInstanceIcon" }; rotate.Click += (_, _) => Model.Rotate(); buttons.Children.Add(rotate);
        var reset = new Button { Content = "复位" }; reset.Click += (_, _) => Model.Reset(); buttons.Children.Add(reset);
        var saving = false; var cancel = new Button { Content = "取消", Name = "CancelInstanceIconCrop" }; cancel.Click += (_, _) => { if (saving) cancelSaving?.Invoke(); else Close(); }; buttons.Children.Add(cancel);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "InstanceIconCropError" };
        var confirm = new Button { Content = "保存图标", Name = "SaveInstanceIconCrop" }; confirm.Click += async (_, _) =>
        {
            saving = true; confirm.IsEnabled = rotate.IsEnabled = reset.IsEnabled = false; foreach (var editor in editors) editor.IsEnabled = false; feedback.IsVisible = false; var done = false;
            try { done = await save(Model.Crop()); if (!done) { feedback.Text = error?.Invoke() ?? "保存失败，原图标保留。"; feedback.IsVisible = true; } }
            finally { saving = false; confirm.IsEnabled = rotate.IsEnabled = reset.IsEnabled = true; foreach (var editor in editors) editor.IsEnabled = true; }
            if (done) Close();
        }; buttons.Children.Add(confirm);
        panel.Children.Add(buttons); panel.Children.Add(feedback); Content = panel;
        Closing += (_, e) => { if (saving) { e.Cancel = true; cancelSaving?.Invoke(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (saving) cancelSaving?.Invoke(); else Close(); e.Handled = true; } };
        Closed += (_, _) => Model.Dispose();
    }
}
