using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() => AvaloniaXamlLoader.Load(this);
    private ToolboxViewModel? Toolbox => (DataContext as ToolsPageViewModel)?.Toolbox;
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
    private async Task<string?> SavePathAsync(string title, string name, string extension)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = name, DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant() + " 文件") { Patterns = ["*." + extension] }]
        });
        return file is null ? null : file.TryGetLocalPath() ?? throw new IOException("请选择本机保存位置。");
    }
    private async Task<string?> PickPngAsync(string title)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("PNG 图像") { Patterns = ["*.png"] }] });
        return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new IOException("请选择本机 PNG 文件。");
    }
    private async void PickDownloadDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true } vm) return;
        try
        {
            var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择下载保存目录", AllowMultiple = false });
            if (folders.Count > 0 && vm.CanUseTools) vm.DownloadDirectory = folders[0].TryGetLocalPath() ?? throw new IOException("请选择本机文件夹。");
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void CreateShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true } vm) return;
        try { if (await SavePathAsync("创建启动器快捷方式", "潮涌核心社启动器." + LauncherShortcut.Extension, LauncherShortcut.Extension) is { } path && vm.CanUseTools) await vm.CreateShortcutAsync(path); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void SavePublicSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true } vm) return;
        try
        {
            var name = vm.SkinPlayerName.Trim();
            if (name.Length is < 3 or > 16 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("请输入 3–16 位正版玩家名。");
            if (await SavePathAsync("保存正版玩家皮肤", name + "-skin.png", "png") is { } path && vm.CanUseTools) await vm.SaveSkinAsync(path);
        }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void PickAvatarSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true } vm) return;
        try { if (await PickPngAsync("选择头像使用的皮肤") is { } path && vm.CanUseTools) await vm.LoadAvatarAsync(path); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void PickItemIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true } vm) return;
        try { if (await PickPngAsync("选择成就物品图标") is { } path && vm.CanUseTools) await vm.LoadItemIconAsync(path); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void SaveAchievement_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true, HasAchievement: true } vm) return;
        try { if (await SavePathAsync("保存成就图片", "achievement.png", "png") is { } path && vm.CanUseTools) await vm.SaveAchievementAsync(path); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
    private async void SaveAvatar_Click(object? sender, RoutedEventArgs e)
    {
        if (Toolbox is not { CanUseTools: true, HasAvatar: true } vm) return;
        try { if (await SavePathAsync("保存皮肤头像", "avatar-" + vm.AvatarSize + ".png", "png") is { } path && vm.CanUseTools) await vm.SaveAvatarAsync(path); }
        catch (Exception ex) { StorageDialogs.ShowError(this, ex); }
    }
}
