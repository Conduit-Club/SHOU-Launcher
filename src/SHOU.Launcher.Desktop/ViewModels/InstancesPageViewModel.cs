using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record InstanceLoaderChoice(string Id, string Name);

public sealed partial class InstancesPageViewModel : PageViewModel
{
    private bool _refreshing;
    private bool _draftLoaded;
    private bool _runtimeDirty;
    private string _instanceName = "原版 1.21.1";
    private string _versionId = "1.21.1";
    private string _fabricLoaderVersion = "0.16.14";
    private InstanceLoaderChoice _selectedLoader = null!;
    private string _javaExecutable = "";
    private decimal _memoryMiB = 2048;
    private GameInstance? _selectedInstance;
    private JavaInstallation? _selectedJava;
    private string _planDiagnostics = "";
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public IReadOnlyList<InstanceLoaderChoice> LoaderChoices { get; } = [new("vanilla", "原版"), new("fabric", "Fabric")];
    public string InstanceName { get => _instanceName; set { SetProperty(ref _instanceName, value); NotifyCommands(); } }
    public string VersionId { get => _versionId; set { SetProperty(ref _versionId, value); OnPropertyChanged(nameof(CreationHint)); NotifyCommands(); } }
    public string FabricLoaderVersion { get => _fabricLoaderVersion; set { SetProperty(ref _fabricLoaderVersion, value); OnPropertyChanged(nameof(CreationHint)); NotifyCommands(); } }
    public InstanceLoaderChoice SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (value is null) return;
            var oldDefaultName = $"{_selectedLoader.Name} {VersionId}";
            if (!SetProperty(ref _selectedLoader, value)) return;
            if (InstanceName == oldDefaultName) InstanceName = $"{value.Name} {VersionId}";
            OnPropertyChanged(nameof(IsFabricCreation)); OnPropertyChanged(nameof(CreationHint)); NotifyCommands();
        }
    }
    public bool IsFabricCreation => SelectedLoader.Id == "fabric";
    public string CreationHint => SelectedLoader.Id switch
    {
        "vanilla" => "创建原版 Minecraft 实例后，再安装并启动。",
        "fabric" when string.IsNullOrWhiteSpace(FabricLoaderVersion) => "请明确填写 Fabric Loader 版本。",
        "fabric" => $"Minecraft {VersionId} + Fabric Loader {FabricLoaderVersion}；创建后安装所需依赖。",
        _ => $"暂不支持加载器 {SelectedLoader.Id}，不会作为原版创建。"
    };
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
            // Filtering a list must not erase the launcher's persisted current instance.
            if (value is null && !_refreshing) return;
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
    public string InstanceSummary => SelectedInstance is { } item ? $"Minecraft {item.VersionId} · {LoaderSummary(item)} · {(item.Installed ? "已安装" : "尚未安装")}\n{item.GameDirectory}" : "尚未选择实例。";
    private static string LoaderSummary(GameInstance item) => item.Loader switch
    {
        "vanilla" => "原版",
        "fabric" => "Fabric Loader " + (string.IsNullOrWhiteSpace(item.LoaderVersion) ? "版本缺失" : item.LoaderVersion),
        _ => "未支持的加载器：" + item.Loader
    };
    private bool SelectedInstanceSupported => SelectedInstance?.Loader is "vanilla" or "fabric";
    public string LaunchPrerequisite
    {
        get
        {
            if (SelectedInstance is null) return "创建或选择一个实例。";
            if (!SelectedInstanceSupported) return $"当前候选不支持加载器 {SelectedInstance.Loader}，不会按原版安装或启动。";
            if (Owner.Home.SelectedServerId != "none") return "本候选尚未开放联机，仅进行单机 / 离线验收；请在首页选择“只启动游戏”。";
            if (!SelectedInstance.Installed) return "先安装/校验实例，再启动游戏。";
            var account = Owner.Snapshot.Accounts.FirstOrDefault(a => a.Id == Owner.Snapshot.SelectedAccountId);
            if (account is null) return "请在账户页添加并选择账户。";
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
        _selectedLoader = LoaderChoices[0];
        CreateInstanceCommand = AsyncCommand(CreateInstanceAsync, () => CanEdit && !string.IsNullOrWhiteSpace(InstanceName) && !string.IsNullOrWhiteSpace(VersionId)
            && (SelectedLoader.Id == "vanilla" || (IsFabricCreation && !string.IsNullOrWhiteSpace(FabricLoaderVersion))));
        DiscoverJavaCommand = AsyncCommand(() => Owner.RunAsync("发现 Java", async ct => { await Service.DiscoverJavaAsync(ct); }), () => CanEdit);
        SaveRuntimeCommand = AsyncCommand(SaveRuntimeAsync, () => CanEdit);
        AutomaticJavaCommand = Command(() => { JavaExecutable = ""; SelectedJava = null; }, () => CanEdit);
        InstallCommand = AsyncCommand(() => Owner.RunAsync("安装/校验实例", ct => Service.InstallInstanceAsync(SelectedInstance!.Id, ct)), () => CanEdit && SelectedInstanceSupported);
        LaunchCommand = AsyncCommand(() => Owner.RunAsync("启动游戏", ct => Service.LaunchAsync(SelectedInstance!.Id, Owner.Home.SelectedServerId, ct)), CanLaunch);
        InspectPlanCommand = AsyncCommand(() => Owner.RunAsync("检查启动计划", async ct =>
        {
            var plan = await Service.BuildLaunchPlanAsync(SelectedInstance!.Id, ct);
            PlanDiagnostics = $"Java：{plan.JavaExecutable}\n架构：{plan.JavaRuntimeArchitecture}\n类路径：{plan.ClasspathEntries.Count} 项\n" + string.Join("\n", plan.RedactedDiagnostics.Select(s => UiPrivacy.Redact(s)));
        }), () => CanEdit && SelectedInstanceSupported && SelectedInstance?.Installed == true);
        InitializeWorkspace();
        InitializeWorldWorkspace();
    }
    private async Task CreateInstanceAsync()
    {
        var loader = SelectedLoader.Id;
        var name = InstanceName.Trim();
        var gameVersion = VersionId.Trim();
        var loaderVersion = FabricLoaderVersion.Trim();
        await Owner.RunAsync("创建实例", async ct =>
        {
            switch (loader)
            {
                case "vanilla": await Service.CreateInstanceAsync(name, gameVersion, ct); break;
                case "fabric": await Service.CreateFabricInstanceAsync(name, gameVersion, loaderVersion, ct); break;
                default: throw new InvalidOperationException("暂不支持此加载器，不会作为原版创建。");
            }
        });
    }
    private bool CanLaunch() => CanEdit && SelectedInstanceSupported && SelectedInstance?.Installed == true && Owner.Home.SelectedServerId == "none" && Owner.Snapshot.Accounts.Any(a => a.Id == Owner.Snapshot.SelectedAccountId);
    private async Task SaveRuntimeAsync()
    {
        if (MemoryMiB is < 512 or > 32768 || decimal.Truncate(MemoryMiB) != MemoryMiB) { Owner.ShowError("内存须为 512–32768 MiB 的整数。"); return; }
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
            // Populate the bound list before selecting a newly published instance; otherwise ListBox writes null back.
            FilterInstances();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == snapshot.SelectedInstanceId);
            if (!JavaInstallations.SequenceEqual(snapshot.JavaInstallations)) { JavaInstallations.Clear(); foreach (var java in snapshot.JavaInstallations) JavaInstallations.Add(java); }
            SelectedJava = JavaInstallations.FirstOrDefault(j => j.ExecutablePath == snapshot.Settings.JavaExecutable);
            if (!_draftLoaded || !_runtimeDirty) { JavaExecutable = snapshot.Settings.JavaExecutable ?? ""; MemoryMiB = snapshot.Settings.MemoryMiB; _draftLoaded = true; }
            OnPropertyChanged(nameof(RuntimeDraftSummary));
            OnPropertyChanged(nameof(LaunchPrerequisite));
        }
        finally { _refreshing = false; }
        RefreshWorkspaceSnapshot(snapshot);
        base.Refresh(snapshot);
    }
    public override void NotifyCommands() { base.NotifyCommands(); OnPropertyChanged(nameof(LaunchPrerequisite)); OnPropertyChanged(nameof(CanImportContent)); ScheduleContentRefresh(); NotifyWorldWorkspace(); }
}
