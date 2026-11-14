using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class AccountAppearanceOverlay : UserControl
{
    public AccountAppearanceOverlay() => AvaloniaXamlLoader.Load(this);
    public void FocusCloseButton() => Dispatcher.UIThread.Post(() => this.FindControl<Button>("CloseAccountAppearanceButton")?.Focus());
    private void Appearance_KeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape && DataContext is AccountsPageViewModel vm) { vm.CloseAppearanceCommand.Execute(null); e.Handled = true; } }
    private void ResetPreview_Click(object? sender, RoutedEventArgs e) => this.FindControl<AccountSkinPreviewControl>("AccountSkinPreview")?.ResetView();
    private static FilePickerFileType SkinPngType() => new("Java 皮肤 PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"], AppleUniformTypeIdentifiers = ["public.png"] };
    private async void PickOnlineSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsPageViewModel vm || vm.CaptureOnlineSkinTarget() is not { } target) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider?.CanOpen != true) throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "为此 Microsoft 角色选择皮肤 PNG", AllowMultiple = false, FileTypeFilter = [SkinPngType()] });
            if (files.Count > 0) await vm.PreviewPickedOnlineSkinAsync(files[0].TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机 PNG 文件。"), target);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
        finally { vm.EndLocalSkinPicker(target); }
    }
    private async void PickOfflineSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsPageViewModel vm || vm.CaptureLocalSkinTarget() is not { } target) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            if (!provider.CanOpen) throw new InvalidOperationException("当前环境无法打开文件选择器。");
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "为此离线角色选择皮肤 PNG", AllowMultiple = false, FileTypeFilter = [SkinPngType()] });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机 PNG 文件。");
            await vm.SavePickedSkinAsync(path, target);
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
        finally { vm.EndLocalSkinPicker(target); }
    }
    private async void SaveSkinPng_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is AccountsPageViewModel vm) await SaveSkinPngAsync(this, vm); }
    internal static async Task SaveSkinPngAsync(Control host, AccountsPageViewModel vm)
    {
        if (vm.AccountSkin is not { } skin || vm.SelectedChannelAccount is not { } account) return;
        // Capture immutable source bytes before the picker. A later account change cannot export a different skin.
        var png = skin.Png.ToArray();
        try
        {
            var provider = TopLevel.GetTopLevel(host)?.StorageProvider ?? throw new InvalidOperationException("当前环境无法打开文件选择器。");
            if (!provider.CanSave) throw new InvalidOperationException("当前环境无法打开保存选择器。");
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "保存所选角色的皮肤 PNG", SuggestedFileName = "skin-" + new string(account.PlayerUuid.Where(char.IsAsciiLetterOrDigit).Take(32).ToArray()) + ".png", DefaultExtension = "png", FileTypeChoices = [SkinPngType()] });
            if (file is null) return;
            var path = file.TryGetLocalPath() ?? throw new InvalidOperationException("请选择本机 PNG 保存位置。");
            await vm.Root.RunInlineAsync("保存皮肤 PNG", ct => SHOU.Launcher.Core.Authentication.AccountSkinExport.SaveAsync(path, png, ct));
        }
        catch (Exception ex) { vm.Root.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
}
