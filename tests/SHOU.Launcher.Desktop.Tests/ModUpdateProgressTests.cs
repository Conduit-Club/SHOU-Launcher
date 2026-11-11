using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ModUpdateProgressTests
{
    [AvaloniaFact]
    public async Task QueryShowsRealCountsAndRejectsLateProgressAfterCancellation()
    {
        var service = new ScanService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        vm.Instances.ShowInstanceSettingsCommand.Execute(null);
        vm.Instances.SelectedSection = vm.Instances.Sections.Single(s => s.Key == "mods");
        var pending = vm.Instances.CheckModUpdatesCommand.ExecuteAsync(null);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Dispatcher.UIThread.RunJobs();
        Assert.Contains("12/494", vm.Instances.ModUpdateStatus); Assert.True(vm.Instances.HasModUpdateStatus);
        vm.CancelOperationCommand.Execute(null); await pending;
        Assert.Contains("已取消", vm.Instances.ModUpdateStatus);
        service.Progress!.Report(new("stale", 494, 494)); Dispatcher.UIThread.RunJobs();
        Assert.Contains("已取消", vm.Instances.ModUpdateStatus); Assert.False(vm.IsBusy); Assert.Empty(vm.Instances.ModUpdates);
    }
    private sealed class ScanService : FakeLauncherService, IInstanceModUpdateService, IInstanceModUpdateProgressService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<PackInstallProgress>? Progress;
        public Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("The progress API should be selected.");
        public async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesWithProgressAsync(string id, IProgress<PackInstallProgress> progress, CancellationToken cancellationToken = default)
        { Progress = progress; progress.Report(new("query", 12, 494, "fixture.jar")); Started.SetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return new([], 0, []); }
        public Task<InstanceModUpdateResult> ApplyInstanceModUpdateAsync(string token, CancellationToken ct = default) => throw new InvalidOperationException("Read-only query must not apply changes.");
    }
}
