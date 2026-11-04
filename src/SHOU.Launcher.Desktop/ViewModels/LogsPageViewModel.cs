using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record LogLineViewModel(DateTimeOffset Timestamp, string Level, string Message)
{
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Text => $"{Time} [{Level}] {Message}";
}

public sealed class LogsPageViewModel(MainWindowViewModel owner) : PageViewModel(owner, "日志", "实时、脱敏的启动器与游戏输出。最多保留最近 2000 行。")
{
    public const int Capacity = 2000;
    public ObservableCollection<LogLineViewModel> Entries { get; } = [];
    private readonly HashSet<LogLineViewModel> _seen = [];
    private bool _followTail = true;
    public bool FollowTail { get => _followTail; set => SetProperty(ref _followTail, value); }
    private string _feedback = "";
    public string Feedback { get => _feedback; set => SetProperty(ref _feedback, value); }
    public bool IsEmpty => Entries.Count == 0;
    public string Summary => $"{Entries.Count} 行 · 复制与导出均使用脱敏内容";
    public string ExportText => string.Join(Environment.NewLine, Entries.Select(e => e.Text));
    internal void Append(LauncherLog log)
    {
        var line = new LogLineViewModel(log.Timestamp, UiPrivacy.Redact(log.Level), UiPrivacy.Redact(log.Message));
        if (!_seen.Add(line)) return;
        Entries.Add(line);
        while (Entries.Count > Capacity) { _seen.Remove(Entries[0]); Entries.RemoveAt(0); }
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ExportText));
    }
    public void ClearDisplay()
    {
        Entries.Clear();
        _seen.Clear();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ExportText));
        Feedback = "已清空当前显示；磁盘日志保留。";
    }
}
