using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class DownloadsView
{
    private DownloadsPageViewModel? _subscribedNavigation;
    private bool _navigationAttached;

    private void DownloadsView_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _navigationAttached = true;
        SubscribeNavigation();
    }
    private void DownloadsView_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _navigationAttached = false;
        UnsubscribeNavigation();
    }
    private void DownloadsView_DataContextChanged(object? sender, EventArgs e)
    {
        if (_navigationAttached) SubscribeNavigation();
    }
    private void SubscribeNavigation()
    {
        UnsubscribeNavigation();
        if (DataContext is not DownloadsPageViewModel vm) return;
        _subscribedNavigation = vm;
        vm.PropertyChanged += NavigationPropertyChanged;
        ResetPageScroll(vm);
    }
    private void UnsubscribeNavigation()
    {
        if (_subscribedNavigation is { } previous) previous.PropertyChanged -= NavigationPropertyChanged;
        _subscribedNavigation = null;
    }
    private void NavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DownloadsPageViewModel vm && e.PropertyName is nameof(DownloadsPageViewModel.SelectedCategory)
            or nameof(DownloadsPageViewModel.IsConfiguration) or nameof(DownloadsPageViewModel.IsResourceDetail))
            ResetPageScroll(vm);
    }
    private void ResetPageScroll(DownloadsPageViewModel vm)
    {
        if (!_navigationAttached || !ReferenceEquals(vm, _subscribedNavigation)) return;
        this.FindControl<ScrollViewer>("DownloadsPageScroller")!.Offset = Vector.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            if (_navigationAttached && ReferenceEquals(vm, _subscribedNavigation))
                this.FindControl<ScrollViewer>("DownloadsPageScroller")!.Offset = Vector.Zero;
        }, DispatcherPriority.Loaded);
    }

    private async void CopyProjectName_Click(object? sender, RoutedEventArgs e) => await CopyProjectTextAsync(link: false);
    private async void CopyProjectLink_Click(object? sender, RoutedEventArgs e) => await CopyProjectTextAsync(link: true);
    private async Task CopyProjectTextAsync(bool link)
    {
        if (DataContext is not DownloadsPageViewModel vm || vm.SelectedProject is not { } project) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。");
            await clipboard.SetTextAsync(link ? project.PageUri.AbsoluteUri : project.Title);
        }
        catch (Exception ex) { vm.Root.ShowError("复制失败：" + UiPrivacy.Redact(ex.Message)); }
    }
}
