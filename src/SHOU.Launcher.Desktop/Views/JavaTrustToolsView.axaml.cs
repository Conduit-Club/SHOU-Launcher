using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class JavaTrustToolsView : UserControl
{
    public JavaTrustToolsView() => AvaloniaXamlLoader.Load(this);

    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm || !vm.CanEdit) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("当前环境不支持文件选择。");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要检查的 Java", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Java 可执行文件") { Patterns = OperatingSystem.IsWindows() ? ["java.exe"] : ["java"] }]
            });
            if (vm.CanEdit && files.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.JavaExecutable = path;
        }
        catch (Exception ex) { vm.Root.ShowError("选择 Java 失败：" + UiPrivacy.Redact(ex.Message)); }
    }
}
