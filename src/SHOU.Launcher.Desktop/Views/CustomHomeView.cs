using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

// Interpret portable presentation and explicit button actions; never load CLR/XAML code.
public sealed class CustomHomeView : UserControl
{
    private HomePageViewModel? owner;
    private CancellationTokenSource? loading;
    private readonly List<Bitmap> bitmaps = [];
    public CustomHomeView()
    { DataContextChanged += (_, _) => Attach(); AttachedToVisualTree += (_, _) => Attach(); DetachedFromVisualTree += (_, _) => Detach(); }
    private void Detach()
    { if (owner is not null) { owner.PropertyChanged -= Changed; owner.SetHomeFileWatching(false); } owner = null; loading?.Cancel(); loading?.Dispose(); loading = null; Content = null; foreach (var image in bitmaps) image.Dispose(); bitmaps.Clear(); }
    private void Attach() { Detach(); owner = DataContext as HomePageViewModel; if (owner is null) return; owner.PropertyChanged += Changed; owner.SetHomeFileWatching(true); Render(); }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(HomePageViewModel.HomeDocument)) Render(); }
    private void Render()
    {
        loading?.Cancel(); loading?.Dispose(); loading = new(); foreach (var bitmap in bitmaps) bitmap.Dispose(); bitmaps.Clear();
        if (owner is not { } vm) return;
        try { var panel = new StackPanel { Spacing = 14 }; foreach (var node in CustomHomeDocument.Parse(vm.HomeDocument)) panel.Children.Add(Build(node, vm, loading.Token)); Content = panel; }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or IOException or ArgumentException or FormatException or OverflowException)
        { Content = new TextBlock { Text = "主页布局读取失败：" + UiPrivacy.Redact(ex.Message), TextWrapping = TextWrapping.Wrap }; }
    }
    private Control Build(HomeElement node, HomePageViewModel vm, CancellationToken ct)
    {
        Control result;
        if (node.Kind == "Text") result = new TextBlock { Text = node.Text, FontSize = node.FontSize, TextWrapping = TextWrapping.Wrap };
        else if (node.Kind == "Image") { var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 600 }; _ = LoadImageAsync(image, node.Source ?? "", vm, ct); result = image; }
        else if (node.Kind == "Button") result = new Button { Content = node.Text, Margin = new(0, 0, 8, 8), Command = new AsyncRelayCommand(async () =>
        {
            try { foreach (var action in node.Actions ?? []) { ct.ThrowIfCancellationRequested(); await ExecuteAsync(action, vm, ct); } }
            catch (OperationCanceledException) { }
            catch (Exception ex) { vm.Root.ShowError("主页操作失败：" + UiPrivacy.Redact(ex.Message)); }
        }) };
        else
        {
            var properties = node.Properties ?? new Dictionary<string, string>();
            Panel children = node.Kind switch
            {
                "Grid" => new Grid { ColumnDefinitions = new(properties.GetValueOrDefault("ColumnDefinitions", "*")), RowDefinitions = new(properties.GetValueOrDefault("RowDefinitions", "Auto")) },
                "WrapPanel" => new WrapPanel { Orientation = properties.GetValueOrDefault("Orientation") == "Vertical" ? Orientation.Vertical : Orientation.Horizontal },
                _ => new StackPanel { Spacing = 8, Orientation = node.Horizontal ? Orientation.Horizontal : Orientation.Vertical }
            };
            foreach (var child in node.Children) children.Children.Add(Build(child, vm, ct));
            if (node.Kind is "Card" or "Border")
            {
                var stack = new StackPanel { Spacing = 10 };
                if (node.Text.Length > 0) { var title = new TextBlock { Text = node.Text }; title.Classes.Add("sectionTitle"); stack.Children.Add(title); }
                stack.Children.Add(children);
                if (node.Kind == "Card" && properties.GetValueOrDefault("CanSwap")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                { stack.Children.Remove(children); result = new Expander { Header = node.Text, Content = children, IsExpanded = !string.Equals(properties.GetValueOrDefault("IsSwaped"), "true", StringComparison.OrdinalIgnoreCase) }; result.Classes.Add("card"); }
                else { var border = new Border { Child = stack }; if (node.Kind == "Card") border.Classes.Add("card"); result = border; }
            }
            else result = children;
        }
        ApplyProperties(result, node.Properties); return result;
    }
    private static void ApplyProperties(Control control, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null) return;
        bool Number(string name, Action<double> apply) { if (!values.TryGetValue(name, out var text) || text == "Auto") return false; if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0) throw new FormatException("主页数值无效：" + name); apply(number); return true; }
        Number("Width", v => control.Width = v); Number("Height", v => control.Height = v); Number("MinWidth", v => control.MinWidth = v); Number("MinHeight", v => control.MinHeight = v); Number("MaxWidth", v => control.MaxWidth = v); Number("MaxHeight", v => control.MaxHeight = v); Number("Opacity", v => control.Opacity = Math.Clamp(v, 0, 1));
        if (values.TryGetValue("Margin", out var margin)) control.Margin = Thickness.Parse(margin);
        if (values.TryGetValue("HorizontalAlignment", out var horizontal) && Enum.TryParse<HorizontalAlignment>(horizontal, out var h)) control.HorizontalAlignment = h;
        if (values.TryGetValue("VerticalAlignment", out var vertical) && Enum.TryParse<VerticalAlignment>(vertical, out var v)) control.VerticalAlignment = v;
        if (values.TryGetValue("IsEnabled", out var enabled) && bool.TryParse(enabled, out var enable)) control.IsEnabled = enable;
        if (values.TryGetValue("Visibility", out var visibility)) control.IsVisible = visibility is not ("Collapsed" or "Hidden");
        if (values.TryGetValue("Name", out var name) && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")) { control.Name = name; Avalonia.Automation.AutomationProperties.SetName(control, name); }
        foreach (var (key, setter) in new (string, Action<int>)[] { ("Grid.Row", n => Grid.SetRow(control, n)), ("Grid.Column", n => Grid.SetColumn(control, n)), ("Grid.RowSpan", n => Grid.SetRowSpan(control, n)), ("Grid.ColumnSpan", n => Grid.SetColumnSpan(control, n)) })
            if (values.TryGetValue(key, out var text) && int.TryParse(text, out var n) && n >= (key.EndsWith("Span", StringComparison.Ordinal) ? 1 : 0)) setter(n);
        if (values.TryGetValue("ToolTip", out var tip)) ToolTip.SetTip(control, tip);
        if (control is Border border) { if (values.TryGetValue("Padding", out var padding)) border.Padding = Thickness.Parse(padding); if (values.TryGetValue("CornerRadius", out var radius)) border.CornerRadius = CornerRadius.Parse(radius); if (Brush(values.GetValueOrDefault("Background")) is { } brush) border.Background = brush; }
        if (control is Panel panel) { if (Brush(values.GetValueOrDefault("Background")) is { } brush) panel.Background = brush; if (panel is StackPanel stack) Number("Spacing", n => stack.Spacing = n); }
        if (control is TextBlock label)
        { if (Brush(values.GetValueOrDefault("Foreground")) is { } brush) label.Foreground = brush; if (values.TryGetValue("FontWeight", out var weight)) label.FontWeight = Enum.Parse<FontWeight>(weight, true); if (values.TryGetValue("FontStyle", out var style) && Enum.TryParse<FontStyle>(style, out var fontStyle)) label.FontStyle = fontStyle; if (values.TryGetValue("TextAlignment", out var align) && Enum.TryParse<TextAlignment>(align, out var a)) label.TextAlignment = a; if (values.GetValueOrDefault("TextWrapping") == "NoWrap") label.TextWrapping = TextWrapping.NoWrap; }
        if (control is Image image && values.TryGetValue("Stretch", out var stretch) && Enum.TryParse<Stretch>(stretch, out var st)) image.Stretch = st;
    }
    private static IBrush? Brush(string? value) => value is null || value.StartsWith('{') ? null : Avalonia.Media.Brush.Parse(value);
    private async Task ExecuteAsync(HomeAction action, HomePageViewModel vm, CancellationToken ct)
    {
        if (action.Type == "CopyText") { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。"); await clipboard.SetTextAsync(action.Data); return; }
        if (action.Type == "ShowDialog")
        {
            if (TopLevel.GetTopLevel(this) is not Window parent) throw new InvalidOperationException("当前环境无法打开消息窗口。");
            var parts = action.Data.Split('|'); var close = new Button { Content = parts.Length > 2 ? parts[2] : Localization.UiLocalization.Instance.Text("确定"), HorizontalAlignment = HorizontalAlignment.Right };
            var dialog = new Window { Title = parts[0].Replace("\\n", "\n"), Width = 430, MinHeight = 160, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new(24), Spacing = 18, Children = { new TextBlock { Text = parts[1].Replace("\\n", "\n"), TextWrapping = TextWrapping.Wrap }, close } } };
            close.Click += (_, _) => dialog.Close(); using var registration = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close())); await dialog.ShowDialog(parent); return;
        }
        await vm.ExecuteCustomHomeActionAsync(action, ct);
    }
    private async Task LoadImageAsync(Image image, string source, HomePageViewModel vm, CancellationToken ct)
    {
        try
        {
            if (vm.Root.Service is not ICustomHomeImageService images) throw new NotSupportedException("当前服务不支持主页图片。");
            var bytes = await images.ReadCustomHomeImageAsync(source, vm.HomeDocumentLocation, vm.IsRemoteHome, ct); ct.ThrowIfCancellationRequested(); using var input = new MemoryStream(bytes); var bitmap = Bitmap.DecodeToWidth(input, 1200);
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; } bitmaps.Add(bitmap); image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException or NotSupportedException) { if (!ct.IsCancellationRequested) ToolTip.SetTip(image, "图片读取失败：" + UiPrivacy.Redact(ex.Message)); }
    }
}
