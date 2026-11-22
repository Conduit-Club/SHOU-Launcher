using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;
namespace SHOU.Launcher.Desktop.Tests;

public sealed class OperationProgressUiTests
{
    [AvaloniaFact]
    public void RealOverlayShowsCurrentFileBytesAndClearsCompletedOperation()
    {
        var service = new FakeLauncherService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            service.Publish(service.Snapshot with { State = "Installing", Downloads = [new("download", "stoneblock.zip", "Downloading", 2 * 1048576, 4 * 1048576)] });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var text = window.FindControl<TextBlock>("OperationTransferSummary")!;
            Assert.True(text.IsVisible); Assert.Contains("stoneblock.zip", text.Text); Assert.Contains("MiB", text.Text);
            service.Publish(service.Snapshot with { State = "Ready", Downloads = [new("download", "stoneblock.zip", "Completed", 4 * 1048576, 4 * 1048576)] });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.False(text.IsVisible);
        }
        finally { window.Close(); }
    }
}
