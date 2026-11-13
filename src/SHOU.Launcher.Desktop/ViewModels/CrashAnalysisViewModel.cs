using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class CrashAnalysisViewModel : PageViewModel, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CrashAnalysisReport? report, lastAutomatic;
    private bool busy, disposed;
    private string text = "选择游戏日志或崩溃报告 ZIP，按旧版规则分析原因。";
    public CrashAnalysisViewModel(MainWindowViewModel owner) : base(owner, "崩溃分析", "游戏日志与错误报告")
    {
        AnalyzeInstanceCommand = AsyncCommand(async () =>
        {
            if (Service.Snapshot.SelectedInstanceId is { } id) await AnalyzeAsync(ct => ((ICrashAnalysisService)Service).AnalyzeInstanceCrashAsync(id, Language, ct));
        }, () => CanAnalyze && Service.Snapshot.SelectedInstanceId is not null);
        OpenInstanceSettingsCommand = Command(() =>
        {
            if (report?.InstanceId is { } id && Service.Snapshot.Instances.FirstOrDefault(i => i.Id == id) is { } instance)
            { Owner.Instances.SelectedInstance = instance; Owner.SelectedPage = Owner.Instances; }
        }, () => HasInstanceAction);
    }
    public bool CanAnalyze => !disposed && !busy && Owner.CanMutate && Service is ICrashAnalysisService;
    public bool HasReport => report is not null;
    public bool HasInstanceAction => report?.SuggestInstanceSettings == true && report.InstanceId is not null;
    public string AnalysisText { get => text; private set => SetProperty(ref text, value); }
    private string Language => Localization.UiLocalization.Instance.Locale;
    public AsyncRelayCommand AnalyzeInstanceCommand { get; }
    public RelayCommand OpenInstanceSettingsCommand { get; }
    public Task AnalyzeFilesAsync(IReadOnlyList<string> files) => AnalyzeAsync(ct => ((ICrashAnalysisService)Service).AnalyzeCrashFilesAsync(files, Language, ct));
    private async Task AnalyzeAsync(Func<CancellationToken, Task<CrashAnalysisReport>> analyze)
    {
        if (!CanAnalyze) return;
        busy = true; NotifyCommands();
        try
        {
            CrashAnalysisReport? result = null;
            if (await Owner.RunInlineAsync("分析崩溃日志", async ct =>
            { using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); result = await analyze(linked.Token); }) && !disposed && result is not null) Show(result);
        }
        finally { busy = false; NotifyCommands(); }
    }
    private void Show(CrashAnalysisReport result)
    {
        report = result;
        AnalysisText = result.Text;
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(HasInstanceAction)); NotifyCommands();
    }
    public Task ExportAsync(string path) => report is { } current ? Owner.RunControlAsync("导出错误报告", ct => ((ICrashAnalysisService)Service).ExportCrashAnalysisAsync(current, path, ct)) : Task.CompletedTask;
    public override void Refresh(LauncherSnapshot snapshot) { if (snapshot.LastCrash is { } last && !ReferenceEquals(last, lastAutomatic)) { lastAutomatic = last; Show(last); } base.Refresh(snapshot); }
    public void Dispose() { disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
