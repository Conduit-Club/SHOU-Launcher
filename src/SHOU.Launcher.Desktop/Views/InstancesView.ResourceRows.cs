using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class InstancesView
{
    private static bool IsRowButton(object? source) => source is Control control &&
        (control is Button || control.GetVisualAncestors().Any(parent => parent is Button));
    private void ContentRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (IsRowButton(e.Source) || sender is not Control { DataContext: InstanceContentRowViewModel row } || DataContext is not InstancesPageViewModel vm) return;
        vm.ToggleContentSelection(row);
    }
    private void ContentList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is InstancesPageViewModel browser && e.Key == Key.Back && browser.ParentContentDirectoryCommand.CanExecute(null))
        { _ = browser.ParentContentDirectoryCommand.ExecuteAsync(null); e.Handled = true; return; }
        if (e.Key == Key.Enter && !IsRowButton(e.Source) && DataContext is InstancesPageViewModel { SelectedContentRow: { IsDirectory: true } folder } vmFolder)
        { _ = vmFolder.OpenContentFolderAsync(folder); e.Handled = true; return; }
        if (e.Key != Key.Space || IsRowButton(e.Source) || DataContext is not InstancesPageViewModel { SelectedContentRow: { } row } vm) return;
        vm.ToggleContentSelection(row); e.Handled = true;
    }
    private async void ContentRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsRowButton(e.Source) || sender is not Control { DataContext: InstanceContentRowViewModel { IsDirectory: true } row } || DataContext is not InstancesPageViewModel vm) return;
        try { await vm.OpenContentFolderAsync(row); e.Handled = true; }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void OpenContentFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: InstanceContentRowViewModel row } || DataContext is not InstancesPageViewModel vm) return;
        try { await vm.OpenContentFolderAsync(row); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}
