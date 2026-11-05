using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private bool _readingDroppedPack;

    private void InitializePackDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Pack_DragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, Pack_Drop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static string? PackPath(IDataTransfer transfer)
    {
        var files = transfer.TryGetFiles()?.Take(2).ToArray();
        if (files is not { Length: 1 } || files[0] is not IStorageFile || files[0].TryGetLocalPath() is not { } path)
            return null;
        var extension = Path.GetExtension(path);
        return extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? path : null;
    }

    private bool CanReadDroppedPack() => !_readingDroppedPack && DataContext is MainWindowViewModel
        { CanMutate: true, Downloads.PackServiceAvailable: true };

    private void Pack_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanReadDroppedPack() && PackPath(e.DataTransfer) is not null
            ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Pack_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (!CanReadDroppedPack() || PackPath(e.DataTransfer) is not { } path || DataContext is not MainWindowViewModel vm)
            return;
        _readingDroppedPack = true;
        e.DragEffects = DragDropEffects.Copy;
        try
        {
            vm.Downloads.SelectCategory("modpack");
            vm.SelectedPage = vm.Downloads;
            await vm.Downloads.ReadLocalPackAsync(path);
        }
        catch (Exception ex)
        {
            vm.ShowError("读取整合包失败：" + UiPrivacy.Redact(ex.Message));
        }
        finally { _readingDroppedPack = false; }
    }
}
