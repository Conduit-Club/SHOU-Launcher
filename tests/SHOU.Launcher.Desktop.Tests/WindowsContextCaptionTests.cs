using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WindowsContextCaptionTests
{
    [AvaloniaTheory]
    [InlineData(880)]
    [InlineData(976)]
    public async Task LongInstanceTitleLeavesAllThreeCaptionButtonsVisibleAndInsideWindow(int width)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows caption layout runs on the Windows CI host.");
        var service = new FakeLauncherService();
        var instance = service.Snapshot.Instances.Single() with { Name = new string('长', 180) + " long instance title" };
        service.Publish(service.Snapshot with { Instances = [instance], SelectedInstanceId = instance.Id });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); await vm.InitializeAsync();
        vm.Instances.ShowInstanceSettingsCommand.Execute(null);
        var window = new MainWindow { DataContext = vm, Width = width }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsContextPage);
            var header = window.FindControl<Grid>("ContextHeader")!;
            var controls = window.FindControl<StackPanel>("CustomWindowControls")!;
            Assert.True(header.IsEffectivelyVisible); Assert.True(controls.IsEffectivelyVisible);
            var headerEnd = header.TranslatePoint(new Point(header.Bounds.Width, 0), window)!.Value.X;
            var controlsStart = controls.TranslatePoint(default, window)!.Value.X;
            Assert.True(headerEnd <= controlsStart);
            foreach (var name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
            {
                var button = window.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEffectivelyEnabled);
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
                Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width);
                Assert.InRange(point.Y, 0, 48 - button.Bounds.Height);
            }
        }
        finally { window.Close(); }
    }
}
