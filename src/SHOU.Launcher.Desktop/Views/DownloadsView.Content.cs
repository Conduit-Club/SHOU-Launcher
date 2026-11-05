using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class DownloadsView
{
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
