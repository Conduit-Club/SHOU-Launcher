using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool homeLaunching;
    private string launchInstanceName = "";
    public bool IsHomeLaunching => homeLaunching;
    public string LaunchInstanceName => Snapshot.LaunchActivity?.InstanceName ?? launchInstanceName;
    public string LaunchStage => Snapshot.LaunchActivity?.Stage ?? (Snapshot.State == "Installing" ? "下载并检查游戏文件" : "准备游戏");
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
        launchInstanceName = instance.Name; homeLaunching = true;
        SelectedPage = Home; NotifyLaunchPresentation();
        try { await RunAsync("启动游戏", action); }
        finally { homeLaunching = false; NotifyState(); }
    }
    private void NotifyLaunchPresentation()
    {
        foreach (var property in new[] { nameof(IsHomeLaunching), nameof(LaunchInstanceName), nameof(LaunchStage),
            nameof(LaunchAuthentication), nameof(IsLaunchProgressIndeterminate), nameof(LaunchProgress), nameof(LaunchTransferSummary) })
            OnPropertyChanged(property);
    }
}
