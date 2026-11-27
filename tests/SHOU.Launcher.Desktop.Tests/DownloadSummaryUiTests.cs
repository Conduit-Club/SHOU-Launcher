using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class DownloadSummaryUiTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    [AvaloniaFact]
    public void BackgroundSnapshotBurstUsesOneUiRefreshAndRetainsTheLatestTotals()
    {
        var fake = new FakeLauncherService(); using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        Dispatcher.UIThread.RunJobs(); var refreshed = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Snapshot)) refreshed++; };
        Assert.True(Task.Run(() =>
        {
            for (var i = 1; i <= 1000; i++) fake.Publish(fake.Snapshot with { DownloadTotals = new(Files: i, Completed: i) });
        }).Wait(TimeSpan.FromSeconds(5)));
        Dispatcher.UIThread.RunJobs(); Assert.Equal(1, refreshed); Assert.Equal(1000, vm.Snapshot.DownloadTotals!.Files);
    }
    [AvaloniaFact]
    public void TerminalFilesWithInitiallyUnknownSizeDoNotKeepSpinning()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var completed = new DownloadRowViewModel(vm, new("done", "empty file", "Completed"));
        Assert.False(completed.IsIndeterminate); Assert.Equal(100, completed.Progress); Assert.DoesNotContain("未知大小", completed.Transfer);
        var cancelled = new DownloadRowViewModel(vm, new("cancelled", "file", "Cancelled")); Assert.False(cancelled.IsIndeterminate);
        var verifying = new DownloadRowViewModel(vm, new("verify", "file", "Verifying", 100, 100)); Assert.True(verifying.IsIndeterminate);
    }
    [AvaloniaFact]
    public void FullTotalsAndOperationHistoryIgnore250VisibleRowsAndUnknownWorkStaysIndeterminate()
    {
        var fake = new FakeLauncherService(); var stats = new DownloadStatistics(Files: 1201, Completed: 1200, Pending: 1, UnknownSizes: 1,
            ReadyBytes: 1073741824, KnownTotalBytes: 2147483648, CachedBytes: 536870912, Connections: 4);
        var history = new DownloadActivitySnapshot("history", "资源下载与安装", "Completed", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, stats);
        fake.Publish(fake.Snapshot with { State = "Installing", Downloads = Enumerable.Range(0, 250).Select(i => new DownloadTaskSnapshot(i.ToString(), "file", "Completed", 10, 10)).ToArray(), DownloadTotals = stats,
            DownloadHistory = [history], DownloadBytesPerSecond = 2097152, DownloadRateUpdatedAt = DateTimeOffset.UtcNow });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher()); vm.ShowDownloadsCommand.Execute(null); vm.Downloads.SelectCategory("tasks");
        var window = new MainWindow { Width = 880, Height = 560, DataContext = vm }; window.Show(); Pump(window);
        try
        {
            Assert.Equal(250, vm.Downloads.Tasks.Count); Assert.Contains("1201", vm.Downloads.Summary);
            Assert.Contains("1201", Find<TextBlock>(window, "TransferFileSummary").Text);
            Assert.Contains("1.00 GiB", Find<TextBlock>(window, "TransferByteSummary").Text);
            Assert.Contains("512.00 MiB", vm.Downloads.TransferByteSummary); Assert.Contains("4 个活动连接", vm.Downloads.TransferConnectionSummary);
            Assert.Contains("250", vm.Downloads.TransferDetailScope); Assert.Equal("1", vm.Downloads.TaskRemainingText);
            Assert.True(vm.Downloads.TaskProgressIsIndeterminate); Assert.Equal("处理中", vm.Downloads.TaskProgressText);
            Assert.Contains("1200/1201", Assert.Single(vm.Downloads.DownloadHistoryRows).Summary);
            Assert.True(Find<TextBlock>(window, "TransferByteSummary").Bounds.Width < 700);
            fake.Publish(fake.Snapshot with { DownloadRateUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-3) }); Pump(window);
            Assert.Equal("0.00 MiB/s", vm.Downloads.TaskSpeedText);
            var finished = stats with { Completed = 1201, Pending = 0, UnknownSizes = 0, ReadyBytes = 2147483648, Connections = 0 };
            fake.Publish(fake.Snapshot with { State = "Ready", DownloadTotals = finished with { Files = 1202, Failed = 1, KnownTotalBytes = 3221225472 },
                DownloadActivity = history with { Statistics = finished }, DownloadBytesPerSecond = 0 }); Pump(window);
            Assert.False(vm.Downloads.TaskProgressIsIndeterminate); Assert.Equal("100.00%", vm.Downloads.TaskProgressText); Assert.Equal("0", vm.Downloads.TaskRemainingText);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SavingSourceEditsRetainsSelectedRowsAndScrollAndDiscardRestoresChangedValues()
    {
        var fake = new FakeLauncherService(); using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = vm }; window.Show();
        vm.SelectedPage = vm.Settings; vm.Settings.SelectSectionCommand.Execute("manage"); Pump(window);
        try
        {
            var selected = vm.Settings.Sources[1]; vm.Settings.SelectedSource = selected;
            selected.Name = "Fixture mirror renamed";
            var scroller = Find<ScrollViewer>(window, "SettingsPageScroller"); scroller.Offset = new(0, scroller.Extent.Height); Pump(window); var offset = scroller.Offset;
            await vm.Settings.SaveCommand.ExecuteAsync(null); Pump(window);
            Assert.Same(selected, vm.Settings.SelectedSource); Assert.Same(selected, vm.Settings.Sources[1]); Assert.Equal(offset, scroller.Offset);
            Assert.Equal("Fixture mirror renamed", selected.Name); Assert.False(vm.Settings.IsDirty);
            selected.Name = "Unsaved edit"; vm.Settings.DiscardDraftCommand.Execute(null); Pump(window);
            Assert.Equal("Fixture mirror renamed", vm.Settings.Sources[1].Name); Assert.False(vm.Settings.IsDirty);
        }
        finally { window.Close(); }
    }
}
