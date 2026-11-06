using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class DownloadsView
{
    private async void SaveMinecraftPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadsPageViewModel vm || !vm.CanEdit || sender is not Button { DataContext: MinecraftPackageRow row }) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法选择保存位置。");
            using var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存 " + row.Title, SuggestedFileName = row.Package.FileName, DefaultExtension = "jar",
                FileTypeChoices = [new FilePickerFileType("Java 归档") { Patterns = ["*.jar"] }]
            });
            if (file?.TryGetLocalPath() is { } path) await vm.SaveMinecraftPackageAsync(row, path);
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}
