using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private bool closeApproved, closePending;
    private TaskCompletionSource<int>? closeChoice;
    private async void GuardWindowClose(object? sender, WindowClosingEventArgs e)
    {
        if (closeApproved || DataContext is not MainWindowViewModel vm) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true;
        try
        {
            if (vm.Settings.IsDirty || vm.Instances.HasUnsavedInstanceChanges)
            {
                var panel = this.FindControl<Border>("WindowCloseOverlay")!;
                var save = this.FindControl<Button>("SaveSettingsAndCloseButton")!;
                save.IsVisible = vm.Settings.IsDirty && !vm.Instances.HasUnsavedInstanceChanges;
                save.IsEnabled = vm.Settings.SaveCommand.CanExecute(null);
                this.FindControl<TextBlock>("WindowClosePrompt")!.Text = vm.Instances.HasUnsavedInstanceChanges ? "实例或存档中有未保存的修改。返回页面保存，或放弃草稿并关闭？" : "设置有未保存的修改。";
                closeChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.IsVisible = true; this.FindControl<Button>("CancelWindowCloseButton")!.Focus();
                var decision = await closeChoice.Task; panel.IsVisible = false; closeChoice = null;
                if (decision == 0) return;
                if (decision == 1)
                {
                    await vm.Settings.SaveCommand.ExecuteAsync(null);
                    if (vm.Settings.IsDirty) return;
                }
            }
            await vm.CancelAndWaitForCloseAsync();
            closeApproved = true; Close();
        }
        catch (Exception ex) { vm.ShowError(ex is TimeoutException ? "操作尚未结束，窗口已保留。请稍后重试关闭。" : UiPrivacy.Redact(ex.Message)); }
        finally { closePending = false; }
    }
    private void SaveSettingsAndClose_Click(object? sender, RoutedEventArgs e) => closeChoice?.TrySetResult(1);
    private void DiscardAndClose_Click(object? sender, RoutedEventArgs e) => closeChoice?.TrySetResult(2);
    private void CancelWindowClose_Click(object? sender, RoutedEventArgs e) => closeChoice?.TrySetResult(0);
    private void ClosePrompt_KeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { closeChoice?.TrySetResult(0); e.Handled = true; } }
}
