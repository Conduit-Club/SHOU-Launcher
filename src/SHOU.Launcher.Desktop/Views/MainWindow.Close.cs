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
            if (vm.Instances.HasUnsavedInstanceChanges)
            {
                var panel = this.FindControl<Border>("WindowCloseOverlay")!;
                this.FindControl<TextBlock>("WindowClosePrompt")!.Text = "实例或存档中有未保存的修改。返回页面保存，或放弃修改并关闭？";
                closeChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.IsVisible = true; this.FindControl<Button>("CancelWindowCloseButton")!.Focus();
                var decision = await closeChoice.Task; panel.IsVisible = false; closeChoice = null;
                if (decision == 0) return;

            }
            await vm.CancelAndWaitForCloseAsync();
            closeApproved = true; Close();
        }
        catch (Exception ex) { vm.ShowError(ex is TimeoutException ? "操作尚未结束，窗口已保留。请稍后重试关闭。" : UiPrivacy.Redact(ex.Message)); }
        finally { closePending = false; }
    }
    private void DiscardAndClose_Click(object? sender, RoutedEventArgs e) => closeChoice?.TrySetResult(2);
    private void CancelWindowClose_Click(object? sender, RoutedEventArgs e) => closeChoice?.TrySetResult(0);
    private void ClosePrompt_KeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { closeChoice?.TrySetResult(0); e.Handled = true; } }
}
