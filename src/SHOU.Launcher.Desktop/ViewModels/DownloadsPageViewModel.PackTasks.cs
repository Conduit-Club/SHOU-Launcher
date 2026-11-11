using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    public ObservableCollection<PackInstallTaskViewModel> PackTasks { get; } = [];
    internal PackInstallTaskViewModel? ActivePackTask { get; set; }
    public bool HasPackTasks => PackTasks.Count > 0;
    public bool ShowTaskSummarySidebar => IsTasksPage;
    public double TaskOverallProgress => ActivePackTask?.OverallProgress ?? PackTasks.LastOrDefault()?.OverallProgress ??
        (Tasks.Sum(t => t.Task.TotalBytes ?? 0) is > 0 and var total ? Tasks.Sum(t => t.Task.BytesReceived) * 100d / total : 0);
    public string TaskProgressText => $"{TaskOverallProgress:0.00}%";
    public string TaskSpeedText => $"{(ActivePackTask?.BytesPerSecond ?? Tasks.Where(t => t.Task.State == "Downloading").Sum(t => t.Task.BytesPerSecond)) / 1048576d:0.00} MiB/s";
    public string TaskRemainingText => ActivePackTask?.RemainingFileText ?? Tasks.Count(t => t.Task.State is "Queued" or "Downloading" or "Verifying").ToString();
    public bool ExpandFileTransfers => !HasPackTasks;
    internal void NotifyPackTasks()
    {
        foreach (var name in new[] { nameof(HasPackTasks), nameof(IsEmpty), nameof(Summary), nameof(TaskOverallProgress), nameof(TaskProgressText), nameof(TaskSpeedText), nameof(TaskRemainingText), nameof(ExpandFileTransfers) }) OnPropertyChanged(name);
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
