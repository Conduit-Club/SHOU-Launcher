using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private bool automaticMemory, lockMemory, httpsOnly, preferOfficialCatalog = true, selectInstalledInstance = true;
    private InstanceIsolation isolation;
    private LauncherVisibility visibility;
    private GameProcessPriority priority;
    private GameIpPreference ipPreference;
    private DownloadSourcePreference sourcePreference;
    private string gameTitle = "", jvmArguments = "";
    private string gameArguments = "", beforeLaunchExecutable = "", beforeLaunchArguments = "";
    private decimal maximumConnections = 8, maximumKiBPerSecond;
    private MemoryStatus? systemMemory;
    private DateTime lastMemoryRead;
    public bool AutomaticMemory { get => automaticMemory; set { if (SetProperty(ref automaticMemory, value)) { MarkDirty(); NotifyMemory(); OnPropertyChanged(nameof(CustomMemory)); } } }
    public bool CustomMemory { get => !AutomaticMemory; set { if (value) AutomaticMemory = false; } }
    public bool LockMemory { get => lockMemory; set { if (SetProperty(ref lockMemory, value)) MarkDirty(); } }
    public InstanceIsolation Isolation { get => isolation; set { if (SetProperty(ref isolation, value)) MarkDirty(); } }
    public LauncherVisibility Visibility { get => visibility; set { if (SetProperty(ref visibility, value)) MarkDirty(); } }
    public GameProcessPriority Priority { get => priority; set { if (SetProperty(ref priority, value)) MarkDirty(); } }
    public GameIpPreference IpPreference { get => ipPreference; set { if (SetProperty(ref ipPreference, value)) MarkDirty(); } }
    public string GameTitle { get => gameTitle; set { if (SetProperty(ref gameTitle, value ?? "")) MarkDirty(); } }
    public string JvmArguments { get => jvmArguments; set { if (SetProperty(ref jvmArguments, value ?? "")) MarkDirty(); } }
    public string GameArguments { get => gameArguments; set { if (SetProperty(ref gameArguments, value ?? "")) MarkDirty(); } }
    public string BeforeLaunchExecutable { get => beforeLaunchExecutable; set { if (SetProperty(ref beforeLaunchExecutable, value ?? "")) MarkDirty(); } }
    public string BeforeLaunchArguments { get => beforeLaunchArguments; set { if (SetProperty(ref beforeLaunchArguments, value ?? "")) MarkDirty(); } }
    public bool CanCustomizeGameTitle => Owner.Snapshot.Platform.OS == LauncherOS.Windows;
    public IReadOnlyList<InstanceIsolation> IsolationChoices { get; } = Enum.GetValues<InstanceIsolation>();
    public IReadOnlyList<LauncherVisibility> VisibilityChoices { get; } = Enum.GetValues<LauncherVisibility>();
    public IReadOnlyList<GameProcessPriority> PriorityChoices => Owner.Snapshot.Platform.OS == LauncherOS.MacOS ? [GameProcessPriority.Normal, GameProcessPriority.BelowNormal] : Enum.GetValues<GameProcessPriority>();
    public IReadOnlyList<GameIpPreference> IpChoices { get; } = Enum.GetValues<GameIpPreference>();
    public IReadOnlyList<DownloadSourcePreference> SourcePreferenceChoices { get; } = Enum.GetValues<DownloadSourcePreference>();
    public DownloadSourcePreference SourcePreference { get => sourcePreference; set { if (SetProperty(ref sourcePreference, value)) { MarkDirty(); OnPropertyChanged(nameof(UseCustomSources)); } } }
    public bool UseCustomSources { get => SourcePreference == DownloadSourcePreference.Custom; set { SourcePreference = value ? DownloadSourcePreference.Custom : DownloadSourcePreference.OfficialFirst; } }
    public bool HttpsOnly { get => httpsOnly; set { if (SetProperty(ref httpsOnly, value)) MarkDirty(); } }
    public bool PreferOfficialCatalog { get => preferOfficialCatalog; set { if (SetProperty(ref preferOfficialCatalog, value)) MarkDirty(); } }
    public bool SelectInstalledInstance { get => selectInstalledInstance; set { if (SetProperty(ref selectInstalledInstance, value)) MarkDirty(); } }
    public decimal MaximumConnections { get => maximumConnections; set { if (SetProperty(ref maximumConnections, value)) MarkDirty(); } }
    public decimal MaximumKiBPerSecond { get => maximumKiBPerSecond; set { if (SetProperty(ref maximumKiBPerSecond, value)) MarkDirty(); } }
    public string SystemMemorySummary => systemMemory is { } memory ? $"已使用 / 已安装内存：{(memory.TotalBytes - memory.AvailableBytes) / 1073741824d:0.#} / {memory.TotalBytes / 1073741824d:0.#} GiB" : "无法读取系统内存；保留指定内存。";
    public double UsedMemoryPercent => systemMemory is { TotalBytes: > 0 } m ? 100d * (m.TotalBytes - m.AvailableBytes) / m.TotalBytes : 0;
    public int EffectiveMemoryMiB => GameSettingsPolicy.MemoryMiB(new("preview", "preview", "preview", Service.Paths.InstancesDirectory), Owner.Snapshot.Settings with { MemoryMiB = (int)MemoryMiB, MinimumMemoryMiB = UseMinimumMemory ? (int)MinimumMemoryMiB : null, LaunchOptions = BuildGameOptions() }, systemMemory);
    public decimal DisplayMemoryMiB { get => AutomaticMemory ? EffectiveMemoryMiB : MemoryMiB; set { if (!AutomaticMemory) MemoryMiB = value; } }
    private GameLaunchOptions BuildGameOptions() => new(AutomaticMemory, Isolation, Visibility, Priority, IpPreference, GameTitle.Trim(), JvmArguments.Trim(), LockMemory,
        GameArguments: GameArguments.Trim(), BeforeLaunchExecutable: BeforeLaunchExecutable.Trim(), BeforeLaunchArguments: BeforeLaunchArguments.Trim());
    private DownloadBehavior BuildDownloadOptions() => new((int)MaximumConnections, (int)MaximumKiBPerSecond, HttpsOnly, SourcePreference, PreferOfficialCatalog, SelectInstalledInstance);
    private void LoadGameOptions(LauncherSettings settings)
    {
        var options = settings.LaunchOptions ?? new();
        AutomaticMemory = options.AutomaticMemory; Isolation = options.Isolation; Visibility = options.Visibility; Priority = options.Priority; IpPreference = options.IpPreference; GameTitle = options.WindowTitle; JvmArguments = options.JvmArguments; LockMemory = options.LockMemory;
        GameArguments = options.GameArguments; BeforeLaunchExecutable = options.BeforeLaunchExecutable; BeforeLaunchArguments = options.BeforeLaunchArguments;
        var downloads = settings.Downloads ?? new();
        MaximumConnections = downloads.MaximumConnections; MaximumKiBPerSecond = downloads.MaximumKiBPerSecond; HttpsOnly = downloads.HttpsOnly; SourcePreference = downloads.SourcePreference; PreferOfficialCatalog = downloads.PreferOfficialCatalog; SelectInstalledInstance = downloads.SelectInstalledInstance;
        RefreshSystemMemory();
    }
    public void RefreshSystemMemory()
    {
        if (DateTime.UtcNow - lastMemoryRead < TimeSpan.FromSeconds(5)) return;
        lastMemoryRead = DateTime.UtcNow;
        systemMemory = (Service as IGameSettingsService)?.ReadMemoryStatus(); NotifyMemory();
    }
    private void NotifyMemory() { OnPropertyChanged(nameof(MemorySummary)); OnPropertyChanged(nameof(SystemMemorySummary)); OnPropertyChanged(nameof(UsedMemoryPercent)); OnPropertyChanged(nameof(EffectiveMemoryMiB)); OnPropertyChanged(nameof(DisplayMemoryMiB)); }
    private bool ValidateGameOptions()
    {
        try
        {
            GameSettingsPolicy.Validate(BuildGameOptions());
            if (MaximumConnections != decimal.Truncate(MaximumConnections) || MaximumConnections is < 1 or > 64 || MaximumKiBPerSecond != decimal.Truncate(MaximumKiBPerSecond) || MaximumKiBPerSecond is < 0 or > 1048576) throw new ArgumentException("请填写有效的下载并发和限速值。");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { Owner.ShowError(ex.Message); return false; }
    }
}
