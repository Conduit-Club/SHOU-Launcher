using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class InstancesView
{
    private InstancesPageViewModel? _workspaceVm;
    private bool _workspaceAttached;
    private void WorkspaceAttached(object? sender, VisualTreeAttachmentEventArgs e) { _workspaceAttached = true; BindWorkspace(); }
    private void WorkspaceDetached(object? sender, VisualTreeAttachmentEventArgs e) { _workspaceAttached = false; UnbindWorkspace(); }
    private void WorkspaceDataContextChanged(object? sender, EventArgs e) { if (_workspaceAttached) BindWorkspace(); }
    private void BindWorkspace()
    {
        UnbindWorkspace();
        if (DataContext is not InstancesPageViewModel vm) return;
        _workspaceVm = vm; vm.FilteredInstances.CollectionChanged += FilteredInstancesChanged; vm.PropertyChanged += WorkspacePropertyChanged;
        RestoreFilteredSelection();
    }
    private void UnbindWorkspace()
    {
        if (_workspaceVm is { } previous) { previous.FilteredInstances.CollectionChanged -= FilteredInstancesChanged; previous.PropertyChanged -= WorkspacePropertyChanged; }
        _workspaceVm = null;
    }
    private void FilteredInstancesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RestoreFilteredSelection();
    private void WorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(InstancesPageViewModel.SelectedSection) or nameof(InstancesPageViewModel.IsSelection))) return;
        this.FindControl<ScrollViewer>("InstancePageScroller")!.Offset = Vector.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            if (_workspaceAttached && ReferenceEquals(sender, _workspaceVm)) this.FindControl<ScrollViewer>("InstancePageScroller")!.Offset = Vector.Zero;
        }, DispatcherPriority.Loaded);
    }
    private void RestoreFilteredSelection()
    {
        var vm = _workspaceVm;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_workspaceAttached || vm is null || !ReferenceEquals(vm, _workspaceVm)) return;
            var selected = vm.SelectedInstance;
            if (selected is not null && vm.FilteredInstances.Contains(selected))
                this.FindControl<ListBox>("InstanceSelector")!.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, selected);
        }, DispatcherPriority.Loaded);
    }
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
