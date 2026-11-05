using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class InstancesView
{
    private void OpenSelectedInstance_DoubleTapped(object? sender, TappedEventArgs e)
    { if (DataContext is InstancesPageViewModel vm && vm.ShowInstanceSettingsCommand.CanExecute(null)) vm.ShowInstanceSettingsCommand.Execute(null); }
    private void OpenSelectedInstance_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not InstancesPageViewModel vm || !vm.ShowInstanceSettingsCommand.CanExecute(null)) return;
        vm.ShowInstanceSettingsCommand.Execute(null); e.Handled = true;
    }
    private async void ImportContent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesPageViewModel vm || !vm.CanImportContent) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入" + vm.SectionTitle, AllowMultiple = true });
            foreach (var file in files)
                await vm.ImportContentFileAsync(file.TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机文件。"));
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}
