using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class InstanceWorldsView : UserControl
{
    private async void CopyInstanceServerAddress_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel vm || sender is not Control { DataContext: InstanceServerDraftViewModel row } || !vm.IsCurrentServerRow(row)) return;
        try { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。"); await clipboard.SetTextAsync(row.Address); }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }

    private InstancesPageViewModel? subscribed;
    private bool editorWasOpen;
    public InstanceWorldsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is not InstancesPageViewModel vm) return;
        subscribed = vm; editorWasOpen = false; vm.PropertyChanged += OnWorkspaceChanged;
    }
    private void Unsubscribe() { if (subscribed is { } vm) vm.PropertyChanged -= OnWorkspaceChanged; subscribed = null; editorWasOpen = false; }
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(InstancesPageViewModel.IsServerEditorOpen) || subscribed is not { } vm) return;
        var opened = vm.IsServerEditorOpen; var newlyOpened = opened && !editorWasOpen; editorWasOpen = opened;
        if (!newlyOpened) return;
        Dispatcher.UIThread.Post(() => { if (subscribed is not { IsServerEditorOpen: true }) return; var input = this.FindControl<TextBox>("InstanceServerNameDraft"); input?.Focus(); input?.SelectAll(); });
    }
    private async void ImportWorldDatapack_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { CanImportWorldDatapack: true } vm || vm.SelectedInstance is not { } instance || vm.SelectedWorldRow is not { } world) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择本机 ZIP 数据包", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("ZIP 数据包") { Patterns = ["*.zip"] }] });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机 ZIP 文件。");
            await vm.ImportWorldDatapackFileAsync(path, instance.Id, world.DirectoryName, instance.GameDirectory);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private async void PasteWorldDatapack_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { CanImportWorldDatapack: true } vm || vm.SelectedInstance is not { } instance || vm.SelectedWorldRow is not { } world) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。");
            var files = await clipboard.TryGetFilesAsync();
            var paths = files?.Select(f => f.TryGetLocalPath() ?? "").ToArray() ?? [];
            await vm.ImportClipboardWorldDatapackAsync(paths, instance.Id, world.DirectoryName, instance.GameDirectory);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private async void ImportLocalWorld_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { CanSelectLocalWorldArchive: true } vm || vm.SelectedInstance is not { } instance) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择本地 Java 世界 ZIP", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Java 世界 ZIP") { Patterns = ["*.zip"] }] });
            if (files.Count == 0) return;
            vm.SelectLocalWorldArchive([files[0].TryGetLocalPath() ?? ""], instance.Id, instance.GameDirectory);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private async void PasteLocalWorld_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { CanSelectLocalWorldArchive: true } vm || vm.SelectedInstance is not { } instance) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。");
            var files = await clipboard.TryGetFilesAsync();
            vm.SelectLocalWorldArchive(files?.Select(f => f.TryGetLocalPath() ?? "").ToArray() ?? [], instance.Id, instance.GameDirectory);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private async void ChooseDatapackReplacement_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { CanChooseLocalDatapackUpdate: true, DatapackUpdateContext: { } context, DatapackUpdatePackId: { } packId } vm) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择替换用的数据包 ZIP", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("数据包 ZIP") { Patterns = ["*.zip"] }] });
            if (files.Count == 0) return;
            await vm.PreviewLocalDatapackUpdateAsync(files[0].TryGetLocalPath() ?? "", context, packId);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private void EditInstanceServer_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel vm || sender is not Button { Tag: InstanceServerDraftViewModel row }) return;
        vm.SelectedInstanceServer = row;
        if (vm.EditInstanceServerCommand.CanExecute(null)) vm.EditInstanceServerCommand.Execute(null);
    }
    private async void CopyWorldSeed_Click(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not InstancesPageViewModel { HasWorldSeed: true } vm) return;
        try { var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。"); await clipboard.SetTextAsync(vm.WorldSeedText); }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private void Workspace_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && DataContext is InstancesPageViewModel { IsServerEditorOpen: true } vm) { vm.CancelInstanceServerEditorCommand.Execute(null); args.Handled = true; }
        else if (args.Key == Key.Escape && DataContext is InstancesPageViewModel manager && (manager.IsDatapackOrderOpen || manager.IsDatapackUpdateOpen || manager.IsDatapackBackupsOpen)) { manager.CancelDatapackManagementCommand.Execute(null); args.Handled = true; }
        else if (args.Key == Key.Escape && DataContext is InstancesPageViewModel { IsLocalWorldImportOpen: true } worlds) { worlds.CancelLocalWorldImportCommand.Execute(null); args.Handled = true; }
    }
}
