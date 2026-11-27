using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public sealed class InstanceArtwork : Control
{
    public static readonly StyledProperty<GameInstance?> InstanceProperty = AvaloniaProperty.Register<InstanceArtwork, GameInstance?>(nameof(Instance));
    public static readonly StyledProperty<IInstanceArtworkService?> ServiceProperty = AvaloniaProperty.Register<InstanceArtwork, IInstanceArtworkService?>(nameof(Service));
    public static readonly StyledProperty<string> LoaderProperty = AvaloniaProperty.Register<InstanceArtwork, string>(nameof(Loader), "minecraft");
    public GameInstance? Instance { get => GetValue(InstanceProperty); set => SetValue(InstanceProperty, value); }
    public IInstanceArtworkService? Service { get => GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public string Loader { get => GetValue(LoaderProperty); set => SetValue(LoaderProperty, value); }
    private Bitmap? bitmap; private CancellationTokenSource? lifetime; private bool attached;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); attached = true; Reload(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { attached = false; lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; bitmap?.Dispose(); bitmap = null; base.OnDetachedFromVisualTree(e); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == InstanceProperty || change.Property == ServiceProperty || change.Property == LoaderProperty) Reload(); }
    private async void Reload()
    {
        if (!attached) return;
        lifetime?.Cancel(); lifetime?.Dispose(); lifetime = new(); var current = lifetime; bitmap?.Dispose(); bitmap = null; InvalidateVisual();
        var reader = Service ?? (this.FindAncestorOfType<InstancesView>()?.DataContext as InstancesPageViewModel)?.ArtworkService;
        if (Instance is not { } instance || reader is not { } service) return;
        try
        {
            var bytes = await Task.Run(() => service.ReadInstanceIconAsync(instance.Id, current.Token), current.Token);
            if (current.IsCancellationRequested || lifetime != current || !attached || bytes is null) return;
            using var stream = new MemoryStream(bytes); bitmap = new Bitmap(stream); InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        { if (!current.IsCancellationRequested && attached) ToolTip.SetTip(this, "图标无法读取，已显示默认图标。"); }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); var icon = Instance?.Presentation?.Icon;
        var key = icon is null or "auto" or "custom" ? Instance?.Loader ?? Loader : icon;
        var image = (IImage?)bitmap ?? InstanceArtworkImages.For(key);
        var side = Math.Min(Bounds.Width, Bounds.Height); context.DrawImage(image, new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side));
    }
}
