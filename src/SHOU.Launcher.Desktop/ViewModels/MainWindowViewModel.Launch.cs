using SHOU.Launcher.Core.Abstractions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool homeLaunching;
    private string launchInstanceName = "";
    private int? dismissedLaunchProcessId;
    private DispatcherTimer? launchWaitTimer;
    public RelayCommand HomeLaunchActionCommand { get; }
    public bool IsWaitingForGameWindow => Snapshot.LaunchActivity?.ProcessId is not null;
    public bool IsHomeLaunching => homeLaunching || IsWaitingForGameWindow && Snapshot.LaunchActivity!.ProcessId != dismissedLaunchProcessId;
    public string LaunchInstanceName => Snapshot.LaunchActivity?.InstanceName ?? launchInstanceName;
    public string LaunchStage => Localization.UiLocalization.Instance.Text(Snapshot.LaunchActivity?.Stage ?? (Snapshot.State == "Installing" ? "下载并检查游戏文件" : "准备游戏"));
    public string LaunchHeading => Localization.UiLocalization.Instance.Text(Snapshot.LaunchActivity?.Stage == "等待游戏窗口" ? "游戏正在初始化" : "正在启动游戏");
    public string HomeLaunchActionCaption => Localization.UiLocalization.Instance.Text(IsWaitingForGameWindow ? "返回启动器" : "取消");
    public string LaunchWaitSummary => Snapshot.LaunchActivity?.ProcessStartedAt is { } started
        ? Localization.UiLocalization.Instance.Format("已等待 {0:0} 秒，正在初始化游戏窗口…", Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds)) : "";
    public string LaunchAuthentication => Snapshot.LaunchActivity?.Authentication ??
        (Snapshot.Accounts.FirstOrDefault(a => a.Id == Snapshot.SelectedAccountId)?.Kind switch
        { AccountKind.Microsoft => "微软正版验证", AccountKind.Yggdrasil => "外置账户验证", _ => "离线角色" });
    public bool IsLaunchProgressIndeterminate => Snapshot.LaunchActivity?.TotalBytes is not > 0;
    public double LaunchProgress => Snapshot.LaunchActivity is { TotalBytes: > 0 } progress
        ? Math.Clamp(100d * progress.BytesReceived / progress.TotalBytes.Value, 0, 100) : 0;
    public string LaunchTransferSummary => Snapshot.LaunchActivity is { TotalBytes: > 0 } progress
        ? $"当前文件 {LaunchProgress:F0}% · {progress.BytesReceived / 1048576d:F1} / {progress.TotalBytes.Value / 1048576d:F1} MiB" : "";
    public async Task RunLaunchAsync(GameInstance instance, Func<CancellationToken, Task> action)
    {
        if (!CanMutate || homeLaunching) return;
        dismissedLaunchProcessId = null; launchInstanceName = instance.Name; homeLaunching = true;
        SelectedPage = Home; NotifyLaunchPresentation();
        try { await RunAsync("启动游戏", action); }
        finally { homeLaunching = false; NotifyState(); }
    }
    private void HomeLaunchAction()
    {
        if (Snapshot.LaunchActivity?.ProcessId is { } pid)
        {
            dismissedLaunchProcessId = pid; homeLaunching = false;
            NotifyState(); // Presentation only. The committed game and its window observer keep running.
        }
        else CancelCurrentOperation();
    }
    private void NotifyLaunchPresentation()
    {
        if (IsWaitingForGameWindow && IsHomeLaunching && !_disposed)
        {
            if (launchWaitTimer is null)
            {
                launchWaitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
                launchWaitTimer.Tick += (_, _) => OnPropertyChanged(nameof(LaunchWaitSummary));
            }
            launchWaitTimer.Start();
        }
        else launchWaitTimer?.Stop();
        foreach (var property in new[] { nameof(IsHomeLaunching), nameof(LaunchInstanceName), nameof(LaunchStage), nameof(LaunchHeading),
            nameof(LaunchAuthentication), nameof(IsLaunchProgressIndeterminate), nameof(LaunchProgress), nameof(LaunchTransferSummary),
            nameof(IsWaitingForGameWindow), nameof(HomeLaunchActionCaption), nameof(LaunchWaitSummary) })
            OnPropertyChanged(property);
        HomeLaunchActionCommand.NotifyCanExecuteChanged();
    }
}
