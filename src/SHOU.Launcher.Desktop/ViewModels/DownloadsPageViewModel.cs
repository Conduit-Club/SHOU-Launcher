using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class DownloadRowViewModel : ObservableObject
{
    public DownloadTaskSnapshot Task { get; private set; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public string Name => Task.Name;
    public string State => Task.State switch { "Queued" => "等待中", "Downloading" => "下载中", "Completed" => "已完成", "Failed" => "失败", "Cancelled" => "已取消", _ => Task.State };
    public bool IsIndeterminate => Task.TotalBytes is null or <= 0;
    public double Progress => Task.TotalBytes is > 0 ? Math.Clamp(Task.BytesReceived * 100d / Task.TotalBytes.Value, 0, 100) : 0;
    public string Transfer => $"{FormatBytes(Task.BytesReceived)} / {(Task.TotalBytes is { } total ? FormatBytes(total) : "未知大小")} · {FormatBytes((long)Task.BytesPerSecond)}/s";
    public string Source => Task.Source is { } source ? UiPrivacy.Redact(source) : "来源尚未确定";
    public string? Error => Task.Error is { } error ? UiPrivacy.Redact(error) : null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public DownloadRowViewModel(MainWindowViewModel owner, DownloadTaskSnapshot task)
    {
        Task = task;
        CancelCommand = new(() => owner.RunControlAsync("取消所属安装", ct => owner.Service.CancelDownloadAsync(Task.Id, ct)), () => Task.State is "Queued" or "Downloading");
        RetryCommand = new(() => owner.RunAsync("重试所属安装", ct => owner.Service.RetryDownloadAsync(Task.Id, ct)), () => owner.CanMutate && Task.State is "Failed" or "Cancelled");
    }
    internal void Update(DownloadTaskSnapshot task)
    {
        if (Task == task) return;
        Task = task;
        foreach (var property in new[] { nameof(Task), nameof(Name), nameof(State), nameof(IsIndeterminate), nameof(Progress), nameof(Transfer), nameof(Source), nameof(Error), nameof(HasError) }) OnPropertyChanged(property);
        CancelCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
    }
    private static string FormatBytes(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
}

public sealed class DownloadsPageViewModel(MainWindowViewModel owner) : PageViewModel(owner, "下载任务", "跟踪真实进度、来源与失败原因；可取消或重试任务所属安装。")
{
    public ObservableCollection<DownloadRowViewModel> Tasks { get; } = [];
    public bool IsEmpty => Tasks.Count == 0;
    public string Summary => $"{Tasks.Count} 项任务 · {Tasks.Count(t => t.Task.State == "Downloading")} 项下载中";
    public override void Refresh(LauncherSnapshot snapshot)
    {
        var ids = snapshot.Downloads.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Tasks.Count - 1; i >= 0; i--) if (!ids.Contains(Tasks[i].Task.Id)) Tasks.RemoveAt(i);
        for (var i = 0; i < snapshot.Downloads.Count; i++)
        {
            var task = snapshot.Downloads[i];
            var row = Tasks.FirstOrDefault(t => t.Task.Id == task.Id);
            if (row is null) Tasks.Insert(i, new(Owner, task));
            else { row.Update(task); if (Tasks.IndexOf(row) != i) Tasks.Move(Tasks.IndexOf(row), i); }
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        base.Refresh(snapshot);
    }
    public override void NotifyCommands()
    {
        foreach (var task in Tasks) { task.CancelCommand.NotifyCanExecuteChanged(); task.RetryCommand.NotifyCanExecuteChanged(); }
        base.NotifyCommands();
    }
}
