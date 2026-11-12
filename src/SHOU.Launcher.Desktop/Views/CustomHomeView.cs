using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

// Interpret the portable presentation subset; never load executable XAML or CLR types.
public sealed class CustomHomeView : UserControl
{
    private HomePageViewModel? owner;
    private CancellationTokenSource? loading;
    private readonly List<Bitmap> bitmaps = [];
    public CustomHomeView()
    {
        DataContextChanged += (_, _) => Attach(); AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }
    private void Detach()
    { if (owner is not null) owner.PropertyChanged -= Changed; owner = null; loading?.Cancel(); loading?.Dispose(); loading = null; Content = null; foreach (var image in bitmaps) image.Dispose(); bitmaps.Clear(); }
    private void Attach() { Detach(); owner = DataContext as HomePageViewModel; if (owner is null) return; owner.PropertyChanged += Changed; Render(); }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(HomePageViewModel.HomeDocument)) Render(); }
    private void Render()
    {
        loading?.Cancel(); loading?.Dispose(); loading = new(); foreach (var bitmap in bitmaps) bitmap.Dispose(); bitmaps.Clear();
        if (owner is not { } vm) return;
        try
        {
            var nodes = CustomHomeDocument.Parse(vm.HomeDocument); var panel = new StackPanel { Spacing = 14 };
            foreach (var node in nodes) panel.Children.Add(Build(node, vm, loading.Token)); Content = panel;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or ArgumentException)
        { Content = new TextBlock { Text = "主页布局读取失败：" + UiPrivacy.Redact(ex.Message), TextWrapping = TextWrapping.Wrap }; }
    }
    private Control Build(HomeElement node, HomePageViewModel vm, CancellationToken ct)
    {
        if (node.Kind == "Text") return new TextBlock { Text = node.Text, FontSize = node.FontSize, TextWrapping = TextWrapping.Wrap };
        if (node.Kind == "Image") { var panel = new StackPanel(); _ = LoadImageAsync(panel, node.Source ?? "", vm, ct); return panel; }
        if (node.Kind == "Button") return new Button { Content = node.Text, Margin = new(0, 0, 8, 8), Command = new AsyncRelayCommand(() => vm.Root.RunControlAsync("打开主页链接", token => vm.Root.ExternalLauncher.OpenUriAsync(new(node.Link!), token))) };
        Panel children = node.Kind == "WrapPanel" ? new WrapPanel() : new StackPanel { Spacing = 8, Orientation = node.Horizontal ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical };
        foreach (var child in node.Children) children.Children.Add(Build(child, vm, ct));
        if (node.Kind != "Card") return children;
        var stack = new StackPanel { Spacing = 10 }; if (node.Text.Length > 0) { var title = new TextBlock { Text = node.Text }; title.Classes.Add("sectionTitle"); stack.Children.Add(title); } stack.Children.Add(children);
        var card = new Border { Child = stack }; card.Classes.Add("card"); return card;
    }
    private async Task LoadImageAsync(StackPanel panel, string source, HomePageViewModel vm, CancellationToken ct)
    {
        try
        {
            if (vm.Root.Service is not ICustomHomeImageService images) throw new NotSupportedException("当前服务不支持主页图片。");
            var bytes = await images.ReadCustomHomeImageAsync(source, vm.HomeDocumentLocation, vm.IsRemoteHome, ct); ct.ThrowIfCancellationRequested();
            using var input = new MemoryStream(bytes); var size = Bitmap.DecodeToWidth(input, 1200);
            if (ct.IsCancellationRequested) { size.Dispose(); return; }
            bitmaps.Add(size); panel.Children.Add(new Image { Source = size, Stretch = Stretch.Uniform, MaxHeight = 600 });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException or NotSupportedException)
        { if (!ct.IsCancellationRequested) panel.Children.Add(new TextBlock { Text = "图片读取失败：" + UiPrivacy.Redact(ex.Message), TextWrapping = TextWrapping.Wrap }); }
    }
}
