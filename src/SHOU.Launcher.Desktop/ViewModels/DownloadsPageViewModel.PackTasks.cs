using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    public ObservableCollection<PackInstallTaskViewModel> PackTasks { get; } = [];
    internal PackInstallTaskViewModel? ActivePackTask { get; set; }
    public bool HasPackTasks => PackTasks.Count > 0;
    public bool ShowTaskSummarySidebar => IsTasksPage;
    private DownloadStatistics TransferTotals => Owner.Snapshot.DownloadTotals ?? new(Files: Tasks.Count,
        Completed: Tasks.Count(t => t.Task.State == "Completed"), Pending: Tasks.Count(t => t.Task.State is "Queued" or "Downloading" or "Verifying" or "Retrying"),
        Downloading: Tasks.Count(t => t.Task.State == "Downloading"), UnknownSizes: Tasks.Count(t => t.Task.TotalBytes is null),
        ReadyBytes: Tasks.Sum(t => t.Task.BytesReceived), KnownTotalBytes: Tasks.Sum(t => t.Task.TotalBytes ?? 0));
    private DownloadStatistics CurrentFileTotals => Owner.Snapshot.DownloadActivity?.Statistics ?? TransferTotals;
    private bool IsFilePreparation => Owner.Snapshot.State is "Installing" or "Launching";
    public bool TaskProgressIsIndeterminate => ActivePackTask is null && IsFilePreparation &&
        (CurrentFileTotals.UnknownSizes > 0 || CurrentFileTotals.Pending == 0 || CurrentFileTotals.Verifying > 0 && CurrentFileTotals.Downloading == 0);
    public double TaskOverallProgress => ActivePackTask?.OverallProgress ??
        (CurrentFileTotals.KnownTotalBytes > 0 ? Math.Clamp(CurrentFileTotals.ReadyBytes * 100d / CurrentFileTotals.KnownTotalBytes, 0, 100) :
            CurrentFileTotals.Files > 0 && CurrentFileTotals.Completed == CurrentFileTotals.Files ? 100 : PackTasks.LastOrDefault()?.OverallProgress ?? 0);
    public string TaskProgressText => TaskProgressIsIndeterminate ? "处理中" :
        ActivePackTask is null && (Owner.Snapshot.DownloadActivity?.State is "Failed" or "Cancelled") ? "未完成" : $"{TaskOverallProgress:0.00}%";
    public string TaskSpeedText => $"{CurrentTransferSpeed / 1048576d:0.00} MiB/s";
    private double CurrentTransferSpeed => Owner.Snapshot.DownloadRateUpdatedAt != default &&
        DateTimeOffset.UtcNow - Owner.Snapshot.DownloadRateUpdatedAt >= TimeSpan.FromSeconds(1.5) ? 0 : Owner.Snapshot.DownloadBytesPerSecond;
    public string TaskRemainingText => ActivePackTask?.RemainingFileText ??
        (IsFilePreparation && CurrentFileTotals.Pending == 0 ? "统计中" : CurrentFileTotals.Pending.ToString());
    public string TransferFileSummary => $"{TransferTotals.Files} 项文件任务 · {TransferTotals.Completed} 已完成 · {TransferTotals.Pending} 待处理 · {TransferTotals.Failed} 失败 · {TransferTotals.Cancelled} 已取消";
    public string TransferByteSummary => $"累计就绪 {Bytes(TransferTotals.ReadyBytes)} / 已知总量 {Bytes(TransferTotals.KnownTotalBytes)} · 缓存复用 {Bytes(TransferTotals.CachedBytes)}";
    public string TransferConnectionSummary => $"{TransferTotals.Connections} 个活动连接 · {TransferTotals.Verifying} 项校验中" +
        (TransferTotals.UnknownSizes > 0 ? $" · {TransferTotals.UnknownSizes} 项大小待解析" : "") + "；解析过程中会继续发现文件。";
    public string TransferDetailScope => $"下方显示最近 {Tasks.Count} 项明细；上方统计包含本次启动的全部文件任务。累计就绪包含续传和缓存，不等同于网络流量。";
    private static string Bytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.00} GiB" : $"{value / 1048576d:0.00} MiB";
    public ObservableCollection<DownloadHistoryRowViewModel> DownloadHistoryRows { get; } = [];
    public bool HasDownloadHistory => DownloadHistoryRows.Count > 0;
    private IReadOnlyList<DownloadActivitySnapshot> lastHistory = [];
    private readonly Avalonia.Threading.DispatcherTimer transferDisplayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private void InitializeTransferSummary()
    {
        transferDisplayTimer.Tick += (_, _) => { if (IsTasksPage) OnPropertyChanged(nameof(TaskSpeedText)); };
        transferDisplayTimer.Start();
    }
    private void RefreshTransferHistory(LauncherSnapshot snapshot)
    {
        if (lastHistory.SequenceEqual(snapshot.DownloadHistory)) return;
        lastHistory = snapshot.DownloadHistory; DownloadHistoryRows.Clear();
        foreach (var item in snapshot.DownloadHistory) DownloadHistoryRows.Add(new(item));
        OnPropertyChanged(nameof(HasDownloadHistory));
    }
    public bool ExpandFileTransfers => !HasPackTasks;
    internal void NotifyPackTasks()
    {
        foreach (var name in new[] { nameof(HasPackTasks), nameof(IsEmpty), nameof(Summary), nameof(TaskOverallProgress), nameof(TaskProgressText), nameof(TaskSpeedText), nameof(TaskRemainingText), nameof(ExpandFileTransfers), nameof(TaskProgressIsIndeterminate), nameof(TransferFileSummary), nameof(TransferByteSummary), nameof(TransferConnectionSummary), nameof(TransferDetailScope) }) OnPropertyChanged(name);
    }
    internal async Task DownloadCatalogPackAsync(ContentFile file, ContentRelease? release)
    {
        if (!CanEdit || _catalog is null || SelectedProject is not { Kind: "modpack" } project) return;
        var previous = PackTasks.LastOrDefault(t => t.Identity.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && (previous.IsActive || previous.IsCompleted))
        {
            Owner.ShowTaskNotification(this, previous.IsCompleted ? "此整合包已安装" : "此整合包正在安装", project.Title + " · 点击查看任务"); return;
        }
        var task = previous ?? new PackInstallTaskViewModel(this, Owner, project, release, file, _catalog, _localPacks, _packs);
        if (previous is null) PackTasks.Add(task);
        await task.RunAsync();
    }
}

public sealed record DownloadHistoryRowViewModel(DownloadActivitySnapshot Activity)
{
    public string Name => Activity.Name;
    public string Status => Activity.State switch { "Completed" => "已完成", "Cancelled" => "已取消", "Failed" => "失败", _ => "进行中" };
    public string Summary => $"{Activity.StartedAt.ToLocalTime():HH:mm:ss} · {Status} · {Activity.Statistics.Completed}/{Activity.Statistics.Files} 项文件完成 · 就绪 {Activity.Statistics.ReadyBytes / 1048576d:0.00} MiB · 缓存 {Activity.Statistics.CachedBytes / 1048576d:0.00} MiB";
}
