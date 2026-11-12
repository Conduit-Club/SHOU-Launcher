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
        if (e.Key != Key.Space || IsRowButton(e.Source) || DataContext is not InstancesPageViewModel { SelectedContentRow: { } row } vm) return;
        vm.ToggleContentSelection(row); e.Handled = true;
    }
}
