using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record JavaTrustFindingViewModel(string Path, string Hash, string Summary, string Details);

public sealed class JavaTrustToolsViewModel : PageViewModel
{
    private readonly IJavaTrustWorkflowService? workflow;
    private string target = "", status = "选择一个 Java 后开始只读检查。", provenance = "", feedback = "";
    private long generation;
    private bool inspected, repairable, preferenceLoaded, autoRepair = true, preferenceDirty, detailsExpanded;
    private JavaTrustDerivation? prepared;
    private JavaTrustSelectionChange? selectionChange;
    public JavaTrustToolsViewModel(MainWindowViewModel owner) : base(owner, "Java 证书", "只读检查、独立副本与默认 Java 选择")
    {
        workflow = Service as IJavaTrustWorkflowService;
        InspectCommand = AsyncCommand(InspectAsync, () => Owner.CanReadRemote && HasExplicitTarget);
        PrepareCopyCommand = AsyncCommand(PrepareAsync, () => CanEdit && CanPrepare);
        SelectCopyCommand = AsyncCommand(SelectAsync, () => CanEdit && CanSelectPrepared);
        RestoreSelectionCommand = AsyncCommand(RestoreAsync, () => CanEdit && HasRollback);
        SavePreferenceCommand = AsyncCommand(SavePreferenceAsync, () => CanEdit && preferenceDirty);
        UseDefaultTargetCommand = Command(() => TargetJava = Owner.Snapshot.Settings.JavaExecutable ?? "", () => CanEdit && !string.IsNullOrWhiteSpace(Owner.Snapshot.Settings.JavaExecutable));
    }
    public string TargetJava
    {
        get => target;
        set
        {
            if (!SetProperty(ref target, value ?? "")) return;
            ++generation; inspected = repairable = false; prepared = null; Findings.Clear(); Provenance = "";
            Status = "尚未扫描当前 Java。检查路径不会更改默认 Java。";
            NotifyPresentation();
        }
    }
    public bool HasExplicitTarget => !string.IsNullOrWhiteSpace(TargetJava) && Path.IsPathFullyQualified(TargetJava.Trim());
    public bool WorkflowAvailable => workflow is not null;
    public bool CanPrepare => WorkflowAvailable && HasExplicitTarget && inspected && repairable && prepared is null;
    public bool HasPrepared => prepared is not null;
    public bool CanSelectPrepared => prepared is { Java.IsUsable: true } copy && !SamePath(copy.Java.ExecutablePath, Owner.Snapshot.Settings.JavaExecutable);
    public string PreparedJava => prepared?.Java.ExecutablePath ?? "";
    public bool HasRollback => selectionChange is not null;
    public string RollbackLabel => selectionChange?.PreviousJavaExecutable is null ? "恢复自动选择" : "恢复之前的默认 Java";
    public string CurrentDefaultJava => Owner.Snapshot.Settings.JavaExecutable ?? "自动选择 Java（由版本需求和实例设置决定）";
    public bool AutoRepair
    {
        get => autoRepair;
        set { if (SetProperty(ref autoRepair, value)) { preferenceDirty = value != Owner.Snapshot.Settings.ConduitJavaTrustAutoRepair; OnPropertyChanged(nameof(PreferenceSummary)); NotifyCommands(); } }
    }
    public string PreferenceSummary => preferenceDirty ? "自动修复偏好尚未保存。" : "自动修复偏好已保存；仅用于以后新下载的 Java。";
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Provenance { get => provenance; private set { SetProperty(ref provenance, value); OnPropertyChanged(nameof(HasProvenance)); } }
    public bool HasProvenance => Provenance.Length > 0;
    public string Feedback { get => feedback; private set => SetProperty(ref feedback, value); }
    public bool DetailsExpanded { get => detailsExpanded; set => SetProperty(ref detailsExpanded, value); }
    public ObservableCollection<JavaTrustFindingViewModel> Findings { get; } = [];
    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand PrepareCopyCommand { get; }
    public AsyncRelayCommand SelectCopyCommand { get; }
    public AsyncRelayCommand RestoreSelectionCommand { get; }
    public AsyncRelayCommand SavePreferenceCommand { get; }
    public RelayCommand UseDefaultTargetCommand { get; }
    private static bool SamePath(string? first, string? second) => string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private bool TryTarget(out string path)
    {
        path = TargetJava.Trim();
        if (HasExplicitTarget) return true;
        Status = "请明确选择 Java 可执行文件的绝对路径。"; return false;
    }
    private async Task InspectAsync()
    {
        if (!TryTarget(out var path)) return;
        var current = generation; inspected = repairable = false; prepared = null; Findings.Clear(); Provenance = "";
        Status = "正在只读检查 Java…"; NotifyPresentation();
        IReadOnlyList<JavaTrustFinding>? results = null;
        var success = await Owner.RunAsync("检查 Java 证书", async ct => { results = await Service.InspectJavaTrustAsync(path, ct); ct.ThrowIfCancellationRequested(); }, true);
        if (current != generation) return;
        if (!success) { Status = "检查失败或已取消，结果未知。可重试，默认 Java 未改变。"; NotifyPresentation(); return; }
        var items = results ?? []; ShowFindings(items);
        inspected = items.Count > 0;
        repairable = inspected && items.All(f => f.CanRepair) && items.Any(f => f.NeedsRepair);
        Status = !inspected || items.Any(f => !f.CanRepair) ? "结果未知：检查不完整或策略不支持，暂不能自动修复。请查看检查详情。"
            : repairable ? "需要修复：检测到已知证书问题，可创建独立修复副本。"
            : "健康：没有发现当前策略识别的证书问题。";
        NotifyPresentation();
    }
    private async Task PrepareAsync()
    {
        if (!TryTarget(out var path) || !CanPrepare) return;
        var current = generation; Status = "正在创建并核验独立副本，尚未更改默认 Java…";
        JavaTrustDerivation? result = null;
        var success = await Owner.RunAsync("创建 Java 修复副本", async ct =>
        {
            result = await workflow!.PrepareJavaTrustCopyAsync(path, ct); ct.ThrowIfCancellationRequested();
            if (!result.Java.IsUsable || !Path.IsPathFullyQualified(result.Java.ExecutablePath)) throw new InvalidDataException("副本未通过 Java 探测。");
        });
        if (current != generation) return;
        if (!success || result is null) { Status = "创建失败或已取消，未选用任何副本。可以重试。"; NotifyPresentation(); return; }
        prepared = result; ShowFindings(result.Findings);
        Provenance = UiPrivacy.Redact($"策略：{result.PolicyId}\n源 Java：{result.SourceJavaExecutable}\n副本：{result.Java.ExecutablePath}\n源 SHA-256：{result.SourceTreeSha256}\n副本 SHA-256：{result.OutputTreeSha256}\n来源记录：{result.ProvenancePath}");
        Status = "独立副本已核验，尚未选用。原始 Java 未修改。"; NotifyPresentation();
    }
    private async Task SelectAsync()
    {
        if (prepared is not { } copy || !CanSelectPrepared) return;
        var current = generation; JavaTrustSelectionChange? change = null;
        var success = await Owner.RunAsync("选用 Java 修复副本", async ct => change = await workflow!.SelectJavaTrustCopyAsync(copy, ct));
        if (success && change is not null) { selectionChange = change; Status = "已选用并保存为默认 Java；实例独立设置保持。可以恢复之前的选择。"; }
        else { if (current == generation) prepared = null; Status = "选用失败或已取消，请重新检查副本；未选用不完整副本。"; }
        NotifyPresentation();
    }
    private async Task RestoreAsync()
    {
        if (selectionChange is not { } change) return;
        var success = await Owner.RunAsync("恢复 Java 选择", ct => workflow!.RestoreJavaTrustSelectionAsync(change, ct));
        if (success) { selectionChange = null; Status = "已恢复之前的 Java 选择，修复副本和来源记录保留。"; }
        else Status = "恢复失败或已取消，未覆盖已保存的 Java 选择。";
        NotifyPresentation();
    }
    private async Task SavePreferenceAsync()
    {
        var value = AutoRepair;
        var success = await Owner.RunAsync("保存 Java 自动修复偏好", ct => Service.SaveSettingsAsync(Service.Snapshot.Settings with { ConduitJavaTrustAutoRepair = value }, ct));
        preferenceDirty = AutoRepair != Owner.Snapshot.Settings.ConduitJavaTrustAutoRepair;
        Feedback = success ? "自动修复偏好已保存。" : "偏好未保存，可以重试。";
        OnPropertyChanged(nameof(PreferenceSummary)); NotifyCommands();
    }
    private void ShowFindings(IReadOnlyList<JavaTrustFinding> findings)
    {
        Findings.Clear();
        foreach (var f in findings) Findings.Add(new(UiPrivacy.Redact(f.Path), f.Hash,
            f.NeedsRepair ? f.CanRepair ? "可在独立副本中修复" : "需要处理，策略不支持" : f.CanRepair ? "未发现已知问题" : "未知，未能检查", UiPrivacy.Redact(f.Details)));
        if (findings.Any(f => f.NeedsRepair || !f.CanRepair)) DetailsExpanded = true;
    }
    private void NotifyPresentation()
    {
        foreach (var name in new[] { nameof(HasExplicitTarget), nameof(CanPrepare), nameof(HasPrepared), nameof(CanSelectPrepared), nameof(PreparedJava), nameof(HasRollback), nameof(RollbackLabel), nameof(CurrentDefaultJava) }) OnPropertyChanged(name);
        NotifyCommands();
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        if (!preferenceLoaded || !preferenceDirty) { autoRepair = snapshot.Settings.ConduitJavaTrustAutoRepair; preferenceLoaded = true; OnPropertyChanged(nameof(AutoRepair)); OnPropertyChanged(nameof(PreferenceSummary)); }
        selectionChange = snapshot.Settings.JavaTrustSelection is { } change && SamePath(snapshot.Settings.JavaExecutable, change.SelectedJavaExecutable) ? change : null;
        NotifyPresentation();
    }
}
