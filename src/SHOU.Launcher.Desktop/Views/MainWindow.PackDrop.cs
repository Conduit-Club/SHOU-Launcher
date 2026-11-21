using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using Avalonia.Controls;
using Avalonia.Layout;

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

    private static string[] DropPaths(IDataTransfer transfer)
    {
        var files = transfer.TryGetFiles()?.ToArray();
        if (files is { Length: > 0 })
            return files.All(f => f is IStorageFile && f.TryGetLocalPath() is not null) ? files.Select(f => f.TryGetLocalPath()!).ToArray() : [];
        return transfer.TryGetText() is { } text && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.IsFile && uri.Host.Length == 0 ? [uri.LocalPath] : [];
    }

    internal static string? DropKind(IReadOnlyList<string> paths, string? section)
    {
        if (paths.Count == 0 || paths.Any(p => !Path.IsPathFullyQualified(p))) return null;
        var extensions = paths.Select(p => Path.GetExtension(p).ToLowerInvariant()).ToArray();
        if (extensions.All(e => e == extensions[0]) && extensions[0] is ".jar" or ".litemod" or ".disabled" or ".old") return "mods";
        if (extensions.All(e => e == extensions[0]) && extensions[0] is ".litematic" or ".nbt" or ".schematic" or ".schem") return "schematics";
        if (paths.Count != 1) return null;
        if (extensions[0] == ".zip" && section is "worlds" or "resourcepacks" or "shaders") return section;
        return extensions[0] is ".zip" or ".mrpack" or ".rar" ? "pack" : null;
    }

    private bool CanReadDroppedPack() => !_readingDroppedPack && DataContext is MainWindowViewModel
        { CanMutate: true };

    private void Pack_DragOver(object? sender, DragEventArgs e)
    {
        var section = DataContext is MainWindowViewModel vm && ReferenceEquals(vm.SelectedPage, vm.Instances) && !vm.Instances.IsSelection ? vm.Instances.SelectedSection.Key : null;
        e.DragEffects = CanReadDroppedPack() && DropKind(DropPaths(e.DataTransfer), section) is not null
            ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Pack_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (!CanReadDroppedPack() || DataContext is not MainWindowViewModel vm)
            return;
        var paths = DropPaths(e.DataTransfer);
        var inInstance = ReferenceEquals(vm.SelectedPage, vm.Instances) && !vm.Instances.IsSelection;
        var section = inInstance ? vm.Instances.SelectedSection.Key : null;
        if (DropKind(paths, section) is not { } kind) return;
        _readingDroppedPack = true;
        e.DragEffects = DragDropEffects.Copy;
        try
        {
            if (kind == "pack")
            {
                vm.Downloads.SelectCategory("modpack"); vm.SelectedPage = vm.Downloads;
                await vm.Downloads.InstallDroppedPackAsync(paths[0]);
            }
            else if (vm.Instances.SelectedInstance is { } instance)
            {
                if (kind == "worlds")
                {
                    if (vm.Instances.SelectLocalWorldArchive(paths, instance.Id, instance.GameDirectory))
                    {
                        await vm.Instances.PreviewLocalWorldImportCommand.ExecuteAsync(null);
                        if (vm.Instances.ConfirmLocalWorldImportCommand.CanExecute(null)) await vm.Instances.ConfirmLocalWorldImportCommand.ExecuteAsync(null);
                    }
                }
                else
                {
                    if (kind == "mods" && !InstanceIdentity.SupportsModDirectory(instance.Loader)) { vm.ShowError("请先选择支持模组的实例。"); return; }
                    if ((!inInstance || section != kind) && !await ConfirmFileDropAsync(instance.Name, paths.Length)) return;
                    await vm.Instances.ImportDroppedContentAsync(instance, kind, paths);
                }
            }
            else vm.ShowError("请先选择目标实例，再拖入资源文件。");
        }
        catch (Exception ex)
        {
            vm.ShowError("导入文件失败：" + UiPrivacy.Redact(ex.Message));
        }
        finally { _readingDroppedPack = false; }
    }

    private Task<bool> ConfirmFileDropAsync(string instanceName, int count)
    {
        var dialog = new Window { Title = Localization.UiLocalization.Instance.Text("安装本地资源"), Width = 430, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = Localization.UiLocalization.Instance.Text("取消") };
        var install = new Button { Content = Localization.UiLocalization.Instance.Text("安装") };
        cancel.Click += (_, _) => dialog.Close(false); install.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 18, Children =
        { new TextBlock { Text = Localization.UiLocalization.Instance.Format("将 {0} 个文件装入 {1}？", count, instanceName), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
          new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, install } } } };
        return dialog.ShowDialog<bool>(this);
    }
}
