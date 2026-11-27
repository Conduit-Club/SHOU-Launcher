namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool homeLaunching;
    public bool IsHomeLaunching => homeLaunching;
    public bool IsLaunchProgressIndeterminate => Snapshot.LaunchActivity?.TotalBytes is not > 0;
    public double LaunchProgress => Snapshot.LaunchActivity is { TotalBytes: > 0 } progress
        ? Math.Clamp(100d * progress.BytesReceived / progress.TotalBytes.Value, 0, 100) : 0;
    public async Task RunLaunchAsync(Func<CancellationToken, Task> action)
    {
        if (!CanMutate || homeLaunching) return;
        homeLaunching = true;
        try { await RunAsync("启动游戏", token => Task.Run(() => action(token), token)); }
        finally { homeLaunching = false; NotifyState(); }
    }
    private void NotifyLaunchPresentation()
    {
        OnPropertyChanged(nameof(IsHomeLaunching));
        OnPropertyChanged(nameof(IsLaunchProgressIndeterminate));
        OnPropertyChanged(nameof(LaunchProgress));
    }
}
