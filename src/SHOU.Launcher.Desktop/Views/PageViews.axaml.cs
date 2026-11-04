using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class HomeView : UserControl { public HomeView() => AvaloniaXamlLoader.Load(this); }
public partial class DownloadsView : UserControl { public DownloadsView() => AvaloniaXamlLoader.Load(this); }

public partial class InstancesView : UserControl
{
    public InstancesView() => AvaloniaXamlLoader.Load(this);
    private async void PickJava_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesPageViewModel vm) return;
        try { if (await StorageDialogs.PickJavaAsync(this) is { } path) vm.JavaExecutable = path; }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}

public partial class AccountsView : UserControl
{
    public AccountsView() => AvaloniaXamlLoader.Load(this);
    private async void CopyDeviceCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsPageViewModel vm || !vm.HasDeviceChallenge) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。");
            await clipboard.SetTextAsync(vm.DeviceCode);
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}

public partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
    private async void PickGameRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择游戏根目录", AllowMultiple = false });
            if (folders.Count > 0) vm.GameRoot = folders[0].TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机文件夹。");
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void PickJava_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try { if (await StorageDialogs.PickJavaAsync(this) is { } path) vm.JavaExecutable = path; }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}

public partial class LogsView : UserControl
{
    private LogsPageViewModel? _subscribedLogs;
    public LogsView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => { if (TopLevel.GetTopLevel(this) is not null) Subscribe(); };
    }
    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is not LogsPageViewModel vm) return;
        _subscribedLogs = vm;
        vm.Entries.CollectionChanged += FollowLogs;
    }
    private void Unsubscribe()
    {
        if (_subscribedLogs is { } vm) vm.Entries.CollectionChanged -= FollowLogs;
        _subscribedLogs = null;
    }
    private void FollowLogs(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedLogs is { FollowTail: true } vm && vm.Entries.Count > 0)
                this.FindControl<ListBox>("LogEntries")?.ScrollIntoView(vm.Entries[^1]);
        });
    }
    private async void CopyLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsPageViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境无法访问剪贴板。");
            await clipboard.SetTextAsync(vm.ExportText);
            vm.Feedback = "已复制脱敏日志。";
        }
        catch (Exception ex) { vm.Feedback = "复制失败：" + UiPrivacy.Redact(ex.Message); }
    }
    private async void ExportLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsPageViewModel vm) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出脱敏日志", SuggestedFileName = $"SHOU-launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExtension = "log", FileTypeChoices = [new FilePickerFileType("日志文件") { Patterns = ["*.log", "*.txt"] }]
            });
            if (file is null) { vm.Feedback = "已取消导出。"; return; }
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.ExportText);
            vm.Feedback = "脱敏日志已导出。";
        }
        catch (Exception ex) { vm.Feedback = "导出失败：" + UiPrivacy.Redact(ex.Message); }
    }
    private void ClearLogs_Click(object? sender, RoutedEventArgs e) { if (DataContext is LogsPageViewModel vm) vm.ClearDisplay(); }
}

internal static class StorageDialogs
{
    public static async Task<string?> PickJavaAsync(Control control)
    {
        var provider = TopLevel.GetTopLevel(control)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 Java 可执行文件", AllowMultiple = false });
        return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机 Java 可执行文件。");
    }
    public static void ShowError(Control control, Exception error)
    {
        if (TopLevel.GetTopLevel(control)?.DataContext is MainWindowViewModel root) root.ShowError(UiPrivacy.Redact(error.Message));
    }
}
