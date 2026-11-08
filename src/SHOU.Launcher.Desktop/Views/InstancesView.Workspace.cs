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
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Views;

public partial class InstancesView
{
    private async void PickInstanceIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesPageViewModel { SelectedInstance: { } instance } vm || !vm.CanEdit || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择实例图标", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }] });
            if (files.Count == 0 || vm.SelectedInstance?.Id != instance.Id) return;
            var path = files[0].TryGetLocalPath() ?? throw new IOException("请选择本机图片。");
            var source = await Task.Run(() => InstanceIconCropModel.ReadImage(path));
            if (vm.SelectedInstance?.Id != instance.Id) return;
            await new InstanceIconCropWindow(source, bytes => vm.SaveCroppedInstanceIconAsync(instance.Id, bytes), () => vm.Root.CancelOperationCommand.Execute(null), () => vm.Root.ErrorMessage).ShowDialog(owner);
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void PickExistingFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesPageViewModel vm || !vm.CanEdit || vm.IsFolderBusy) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开目录选择器。");
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择已有 Minecraft 或实例目录", AllowMultiple = false });
            if (folders.Count > 0) await vm.InspectExistingFolderAsync(folders[0].TryGetLocalPath() ?? throw new IOException("请选择本机目录。"));
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void CopyScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesPageViewModel vm || sender is not Control { DataContext: InstanceScreenshotPreview item }) return;
        try { await vm.CopyScreenshotAsync(item, TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问图片剪贴板。")); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
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
    { if (DataContext is InstancesPageViewModel vm && sender is ListBox list && list.SelectedItem is GameInstance selected && vm.OpenVisibleInstanceCommand.CanExecute(selected)) vm.OpenVisibleInstanceCommand.Execute(selected); }
    private void OpenSelectedInstance_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not InstancesPageViewModel vm || sender is not ListBox list || list.SelectedItem is not GameInstance selected || !vm.OpenVisibleInstanceCommand.CanExecute(selected)) return;
        vm.OpenVisibleInstanceCommand.Execute(selected); e.Handled = true;
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
    private async void OpenSavesDirectory_Click(object? sender, RoutedEventArgs e) => await OpenInstanceSubdirectoryAsync("saves");
    private async void OpenModsDirectory_Click(object? sender, RoutedEventArgs e) => await OpenInstanceSubdirectoryAsync("mods");
    private Task OpenInstanceSubdirectoryAsync(string name) => DataContext is InstancesPageViewModel { SelectedInstance: not null } vm
        ? vm.OpenInstanceFolderAsync(name == "saves" ? "worlds" : name) : Task.CompletedTask;
    private void RenameEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not InstancesPageViewModel vm) return;
        vm.CancelRenameCommand.Execute(null); e.Handled = true;
    }
}
