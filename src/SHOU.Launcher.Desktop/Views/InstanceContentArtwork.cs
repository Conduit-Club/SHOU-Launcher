using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Views;

public sealed class InstanceContentArtwork : Image
{
    public static readonly StyledProperty<InstanceContentEntry?> EntryProperty = AvaloniaProperty.Register<InstanceContentArtwork, InstanceContentEntry?>(nameof(Entry));
    public InstanceContentEntry? Entry { get => GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    private Bitmap? bitmap;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == EntryProperty) Load(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Load(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Source = null; bitmap?.Dispose(); bitmap = null; base.OnDetachedFromVisualTree(e); }
    private void Load()
    {
        Source = null; bitmap?.Dispose(); bitmap = null;
        if (Entry?.IconPng is not { Length: > 0 } png) return;
        try { using var stream = new MemoryStream(png, false); bitmap = Bitmap.DecodeToWidth(stream, 80); Source = bitmap; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
    }
}
