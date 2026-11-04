using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstancesPageViewModel : PageViewModel
{
    private bool _refreshing;
    private bool _draftLoaded;
    private bool _runtimeDirty;
    private string _instanceName = "原版 1.21.1";
    private string _versionId = "1.21.1";
    private string _javaExecutable = "";
    private decimal _memoryMiB = 2048;
    private GameInstance? _selectedInstance;
    private JavaInstallation? _selectedJava;
    private string _planDiagnostics = "";
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public string InstanceName { get => _instanceName; set { SetProperty(ref _instanceName, value); NotifyCommands(); } }
    public string VersionId { get => _versionId; set { SetProperty(ref _versionId, value); NotifyCommands(); } }
    public string JavaExecutable { get => _javaExecutable; set { if (SetProperty(ref _javaExecutable, value) && !_refreshing) { _runtimeDirty = true; OnPropertyChanged(nameof(RuntimeDraftSummary)); } } }
    public decimal MemoryMiB { get => _memoryMiB; set { if (SetProperty(ref _memoryMiB, value) && !_refreshing) { _runtimeDirty = true; OnPropertyChanged(nameof(RuntimeDraftSummary)); } } }
    public string RuntimeDraftSummary => _runtimeDirty ? "Java 与内存有未应用的修改。" : "当前显示已保存的全局 Java 与内存。";
    public string PlanDiagnostics { get => _planDiagnostics; private set { SetProperty(ref _planDiagnostics, value); OnPropertyChanged(nameof(HasPlanDiagnostics)); } }
    public bool HasPlanDiagnostics => PlanDiagnostics.Length > 0;
    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!_refreshing && !Owner.CanMutate) { OnPropertyChanged(nameof(SelectedInstance)); return; }
            if (!SetProperty(ref _selectedInstance, value)) return;
            OnPropertyChanged(nameof(InstanceSummary));
            OnPropertyChanged(nameof(LaunchPrerequisite));
            NotifyCommands();
            if (!_refreshing && value is not null)
                _ = Owner.RunAsync("选择实例", ct => Service.SelectInstanceAsync(value.Id, ct));
        }
    }
    public JavaInstallation? SelectedJava
    {
        get => _selectedJava;
        set { if (SetProperty(ref _selectedJava, value) && value is not null && !_refreshing) JavaExecutable = value.ExecutablePath; }
    }
    public string InstanceSummary => SelectedInstance is { } item ? $"{item.VersionId} · {item.Loader} · {(item.Installed ? "已安装" : "尚未安装")}\n{item.GameDirectory}" : "尚未选择实例。";
    public string LaunchPrerequisite
    {
        get
        {
            if (SelectedInstance is null) return "创建或选择一个实例。";
            if (!SelectedInstance.Installed) return "先安装/校验实例，再启动游戏。";
            var account = Owner.Snapshot.Accounts.FirstOrDefault(a => a.Id == Owner.Snapshot.SelectedAccountId);
            if (account is null) return "请在账户页添加并选择账户。";
            if (account.Kind == AccountKind.Offline && Owner.Home.SelectedServerId != "none") return "离线账户请先在首页选择“只启动游戏”。";
            return Owner.Home.DestinationSummary;
        }
    }
    public AsyncRelayCommand CreateInstanceCommand { get; }
    public AsyncRelayCommand DiscoverJavaCommand { get; }
    public AsyncRelayCommand SaveRuntimeCommand { get; }
    public RelayCommand AutomaticJavaCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand LaunchCommand { get; }
    public AsyncRelayCommand InspectPlanCommand { get; }
    public AsyncRelayCommand StopGameCommand => Owner.StopGameCommand;

    public InstancesPageViewModel(MainWindowViewModel owner) : base(owner, "实例与启动", "创建实例，选择运行时，然后安装并启动 Minecraft。")
    {
        CreateInstanceCommand = AsyncCommand(() => Owner.RunAsync("创建实例", async ct => { await Service.CreateInstanceAsync(InstanceName.Trim(), VersionId.Trim(), ct); }), () => CanEdit && !string.IsNullOrWhiteSpace(InstanceName) && !string.IsNullOrWhiteSpace(VersionId));
        DiscoverJavaCommand = AsyncCommand(() => Owner.RunAsync("发现 Java", async ct => { await Service.DiscoverJavaAsync(ct); }), () => CanEdit);
        SaveRuntimeCommand = AsyncCommand(SaveRuntimeAsync, () => CanEdit);
        AutomaticJavaCommand = Command(() => { JavaExecutable = ""; SelectedJava = null; }, () => CanEdit);
        InstallCommand = AsyncCommand(() => Owner.RunAsync("安装/校验实例", ct => Service.InstallInstanceAsync(SelectedInstance!.Id, ct)), () => CanEdit && SelectedInstance is not null);
        LaunchCommand = AsyncCommand(() => Owner.RunAsync("启动游戏", ct => Service.LaunchAsync(SelectedInstance!.Id, Owner.Home.SelectedServerId, ct)), CanLaunch);
        InspectPlanCommand = AsyncCommand(() => Owner.RunAsync("检查启动计划", async ct =>
        {
            var plan = await Service.BuildLaunchPlanAsync(SelectedInstance!.Id, ct);
            PlanDiagnostics = $"Java：{plan.JavaExecutable}\n架构：{plan.JavaRuntimeArchitecture}\n类路径：{plan.ClasspathEntries.Count} 项\n" + string.Join("\n", plan.RedactedDiagnostics.Select(s => UiPrivacy.Redact(s)));
        }), () => CanEdit && SelectedInstance?.Installed == true);
    }
    private bool CanLaunch() => CanEdit && SelectedInstance?.Installed == true && Owner.Snapshot.Accounts.Any(a => a.Id == Owner.Snapshot.SelectedAccountId && (a.Kind != AccountKind.Offline || Owner.Home.SelectedServerId == "none"));
    private async Task SaveRuntimeAsync()
    {
        if (MemoryMiB is < 512 or > 65536 || decimal.Truncate(MemoryMiB) != MemoryMiB) { Owner.ShowError("内存须为 512–65536 MiB 的整数。"); return; }
        var java = JavaExecutable.Trim();
        if (java.Length > 0 && !Path.IsPathFullyQualified(java)) { Owner.ShowError("Java 路径须为绝对路径；留空使用自动选择。"); return; }
        if (await Owner.RunAsync("保存全局 Java 与内存", ct => Service.SaveSettingsAsync(Owner.Snapshot.Settings with { JavaExecutable = java.Length == 0 ? null : java, MemoryMiB = (int)MemoryMiB }, ct)))
        {
            _runtimeDirty = false; Refresh(Service.Snapshot);
        }
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        _refreshing = true;
        try
        {
            if (!Instances.SequenceEqual(snapshot.Instances)) { Instances.Clear(); foreach (var item in snapshot.Instances) Instances.Add(item); }
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == snapshot.SelectedInstanceId);
            if (!JavaInstallations.SequenceEqual(snapshot.JavaInstallations)) { JavaInstallations.Clear(); foreach (var java in snapshot.JavaInstallations) JavaInstallations.Add(java); }
            SelectedJava = JavaInstallations.FirstOrDefault(j => j.ExecutablePath == snapshot.Settings.JavaExecutable);
            if (!_draftLoaded || !_runtimeDirty) { JavaExecutable = snapshot.Settings.JavaExecutable ?? ""; MemoryMiB = snapshot.Settings.MemoryMiB; _draftLoaded = true; }
            OnPropertyChanged(nameof(RuntimeDraftSummary));
            OnPropertyChanged(nameof(LaunchPrerequisite));
        }
        finally { _refreshing = false; }
        base.Refresh(snapshot);
    }
    public override void NotifyCommands() { base.NotifyCommands(); OnPropertyChanged(nameof(LaunchPrerequisite)); }
}
