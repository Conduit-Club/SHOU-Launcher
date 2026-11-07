using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class SettingsView
{
    private IStorageProvider Files => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
    private async void ChooseBackgrounds_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try
        {
            var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Localization.UiLocalization.Instance.Text("选择首页轮播图片"), AllowMultiple = true, FileTypeFilter = [new("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }] });
            if (files.Count > 0 && vm.CanEdit) vm.SetBackgroundImages(files.Select(f => f.TryGetLocalPath() ?? throw new IOException("请选择本机图片。")).ToArray());
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void ChooseTitleImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try { var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Localization.UiLocalization.Instance.Text("选择标题栏图片"), AllowMultiple = false, FileTypeFilter = [new("图片") { Patterns = ["*.png", "*.jpg", "*.webp"] }] }); if (files.Count > 0 && vm.CanEdit) vm.CustomTitleImage = files[0].TryGetLocalPath() ?? throw new IOException("请选择本机图片。"); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void ChooseHomeDocument_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try { var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Localization.UiLocalization.Instance.Text("选择本地主页"), AllowMultiple = false, FileTypeFilter = [new("TXT / Markdown") { Patterns = ["*.txt", "*.md"] }] }); if (files.Count > 0 && vm.CanEdit) { vm.HomeSource = "local"; vm.HomeLocation = files[0].TryGetLocalPath() ?? throw new IOException("请选择本机文件。"); } }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void ExportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try { if (await SaveNewFileAsync("导出启动器设置", "SHOU-settings.json", "json", vm.ExportSettings(), vm)) vm.ShowLauncherFeedback("设置已导出；账户和连接密钥未包含在内。"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void ImportSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try
        {
            var files = await Files.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Localization.UiLocalization.Instance.Text("导入启动器设置"), AllowMultiple = false, FileTypeFilter = [new("SHOU 设置") { Patterns = ["*.json"] }] });
            if (files.Count == 0 || !vm.CanEdit) return;
            var path = files[0].TryGetLocalPath() ?? throw new IOException("请选择本机文件。"); SHOU.Launcher.Core.IO.SafePath.RejectLinks(path);
            if (new FileInfo(path).Length > 1048576) throw new IOException("设置文件不能超过 1 MiB。");
            vm.ImportSettings(await File.ReadAllBytesAsync(path, vm.SettingsLifetime));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void ExportAllLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm) return;
        try { if (await SaveNewFileAsync("导出全部脱敏日志", "SHOU-logs.zip", "zip", ct => LauncherLogArchive.ExportAsync(vm.LogsDirectory, ct), vm)) vm.ShowLauncherFeedback("脱敏日志已导出。分享前请检查内容。"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private Task<bool> SaveNewFileAsync(string title, string name, string extension, byte[] data, SettingsPageViewModel vm)
        => SaveNewFileAsync(title, name, extension, _ => Task.FromResult(data), vm);
    private async Task<bool> SaveNewFileAsync(string title, string name, string extension, Func<CancellationToken, Task<byte[]>> prepare, SettingsPageViewModel vm)
    {
        var file = await Files.SaveFilePickerAsync(new FilePickerSaveOptions { Title = Localization.UiLocalization.Instance.Text(title), SuggestedFileName = name, DefaultExtension = extension });
        if (file is null) return false; vm.SettingsLifetime.ThrowIfCancellationRequested();
        var path = file.TryGetLocalPath() ?? throw new IOException("请选择本机保存位置。"); SHOU.Launcher.Core.IO.SafePath.RejectLinks(path);
        return await vm.RunFileOperationAsync(async ct =>
        {
            var data = await prepare(ct); ct.ThrowIfCancellationRequested();
            var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".shou-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            SHOU.Launcher.Core.IO.SafePath.RejectLinks(temporary);
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, true)) { await stream.WriteAsync(data, ct); await stream.FlushAsync(ct); }
                ct.ThrowIfCancellationRequested(); SHOU.Launcher.Core.IO.SafePath.RejectLinks(path); File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }
}
