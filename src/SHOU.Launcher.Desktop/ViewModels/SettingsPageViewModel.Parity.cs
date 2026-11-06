using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record SettingsLogFile(string Name, string Modified, string Size, string Path);

public sealed partial class SettingsPageViewModel
{
    public static IReadOnlyList<string> SectionIds { get; } = ["launch", "java", "manage", "appearance", "language", "misc", "about", "updates", "feedback", "logs"];
    private string _selectedSection = "launch";
    public string SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SectionIds.Contains(value) || !SetProperty(ref _selectedSection, value)) return;
            foreach (var property in new[] { nameof(IsLaunchSection), nameof(IsJavaSection), nameof(IsManageSection), nameof(IsAppearanceSection), nameof(IsLanguageSection), nameof(IsMiscSection), nameof(IsAboutSection), nameof(IsUpdatesSection), nameof(IsFeedbackSection), nameof(IsLogsSection) }) OnPropertyChanged(property);
            if (IsLogsSection) RefreshLogFiles();
        }
    }
    public bool IsLaunchSection => SelectedSection == "launch";
    public bool IsJavaSection => SelectedSection == "java";
    public bool IsManageSection => SelectedSection == "manage";
    public bool IsAppearanceSection => SelectedSection == "appearance";
    public bool IsLanguageSection => SelectedSection == "language";
    public bool IsMiscSection => SelectedSection == "misc";
    public bool IsAboutSection => SelectedSection == "about";
    public bool IsUpdatesSection => SelectedSection == "updates";
    public bool IsFeedbackSection => SelectedSection == "feedback";
    public bool IsLogsSection => SelectedSection == "logs";
    public RelayCommand<string> SelectSectionCommand { get; private set; } = null!;

    private bool _useCustomWindow;
    private decimal _gameWindowWidth = 854;
    private decimal _gameWindowHeight = 480;
    private bool _gameFullscreen;
    private bool _useMinimumMemory;
    private decimal _minimumMemoryMiB = 512;
    private string _customInfo = "";
    public bool UseCustomWindow { get => _useCustomWindow; set { if (SetProperty(ref _useCustomWindow, value)) MarkDirty(); } }
    public decimal GameWindowWidth { get => _gameWindowWidth; set { if (SetProperty(ref _gameWindowWidth, value)) MarkDirty(); } }
    public decimal GameWindowHeight { get => _gameWindowHeight; set { if (SetProperty(ref _gameWindowHeight, value)) MarkDirty(); } }
    public bool GameFullscreen { get => _gameFullscreen; set { if (SetProperty(ref _gameFullscreen, value)) MarkDirty(); } }
    public bool UseMinimumMemory { get => _useMinimumMemory; set { if (SetProperty(ref _useMinimumMemory, value)) MarkDirty(); } }
    public decimal MinimumMemoryMiB { get => _minimumMemoryMiB; set { if (SetProperty(ref _minimumMemoryMiB, value)) MarkDirty(); } }
    public string CustomInfo { get => _customInfo; set { if (SetProperty(ref _customInfo, value)) MarkDirty(); } }
    public string MemorySummary => $"游戏分配：{MemoryMiB / 1024:0.##} GiB（{MemoryMiB:0} MiB）";
    public string JavaSelectionSummary => string.IsNullOrWhiteSpace(JavaExecutable) ? "自动选择 Java（推荐）" : "已指定全局 Java";
    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    private JavaInstallation? _selectedJava;
    private bool _aria2DetailsExpanded;
    public bool Aria2DetailsExpanded { get => _aria2DetailsExpanded; set => SetProperty(ref _aria2DetailsExpanded, value); }
    public JavaInstallation? SelectedJava { get => _selectedJava; set { if (SetProperty(ref _selectedJava, value)) UseSelectedJavaCommand.NotifyCanExecuteChanged(); } }
    public string DiscoveredJavaTitle => $"已发现的 Java（{JavaInstallations.Count}）";
    public AsyncRelayCommand DiscoverJavaCommand { get; private set; } = null!;
    public RelayCommand UseAutomaticJavaCommand { get; private set; } = null!;
    public RelayCommand UseSelectedJavaCommand { get; private set; } = null!;

    private SettingsPresentationStore _presentationStore = null!;
    private SettingsPresentationPreferences _savedPresentation = new();
    private bool _showClubIntroduction;
    private bool _showClubServers;
    private bool _rotateHomeBackground = true;
    private decimal _windowOpacity = 100;
    public bool ShowClubIntroduction { get => _showClubIntroduction; set { if (SetProperty(ref _showClubIntroduction, value)) MarkDirty(); } }
    public bool ShowClubServers { get => _showClubServers; set { if (SetProperty(ref _showClubServers, value)) MarkDirty(); } }
    public bool RotateHomeBackground { get => _rotateHomeBackground; set { if (SetProperty(ref _rotateHomeBackground, value)) MarkDirty(); } }
    public decimal WindowOpacity { get => _windowOpacity; set { if (SetProperty(ref _windowOpacity, value)) MarkDirty(); } }
    public string SoftwareName => Owner.Snapshot.Build.Name;
    public string SoftwareVersion => $"发行版本 v{Owner.Snapshot.Build.Version} · 代号 {Owner.Snapshot.Build.Codename}";
    public string BuildDetails => $"{Owner.Snapshot.Build.Configuration} · {Owner.Snapshot.Build.SourceSha}\n{RuntimeInformation.FrameworkDescription} · {Owner.Snapshot.Platform.OS} / {Owner.Snapshot.Platform.ProcessArchitecture}";
    public string CredentialCapability => Owner.Snapshot.CanPersistAccounts ? "系统安全存储可用" : "系统安全存储不可用";
    public string UpdateCapability => "在线版本检查尚未接入；请在本社团仓库的发行页核对版本。";
    public string FeedbackCapability => "反馈列表尚未接入；可打开本社团仓库的问题列表。";
    public ObservableCollection<SettingsLogFile> LogFiles { get; } = [];
    private string _logFilesSummary = "尚未读取日志目录。";
    public string LogFilesSummary { get => _logFilesSummary; private set => SetProperty(ref _logFilesSummary, value); }
    public RelayCommand RefreshLogFilesCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenLogsDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenProjectCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenReleasesCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenFeedbackCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenClubCommand { get; private set; } = null!;
    public RelayCommand ShowLiveLogsCommand { get; private set; } = null!;

    private void InitializeParity()
    {
        SelectSectionCommand = new(section => { if (section is not null) SelectedSection = section; });
        DiscoverJavaCommand = AsyncCommand(async () =>
        {
            IReadOnlyList<JavaInstallation>? found = null;
            if (await Owner.RunAsync("发现本机 Java", async ct => found = await Service.DiscoverJavaAsync(ct), true) && found is not null) UpdateJava(found);
        }, () => Owner.CanReadRemote);
        UseAutomaticJavaCommand = Command(() => JavaExecutable = "", () => CanEdit);
        UseSelectedJavaCommand = Command(() => JavaExecutable = SelectedJava!.ExecutablePath, () => CanEdit && SelectedJava is { IsUsable: true });
        RefreshLogFilesCommand = Command(RefreshLogFiles);
        OpenLogsDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开日志目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(LogsDirectory, ct)));
        OpenProjectCommand = OpenLink("打开社团源码", "https://github.com/Conduit-Club/SHOU-Launcher");
        OpenReleasesCommand = OpenLink("打开发行页", "https://github.com/Conduit-Club/SHOU-Launcher/releases");
        OpenFeedbackCommand = OpenLink("打开问题列表", "https://github.com/Conduit-Club/SHOU-Launcher/issues");
        OpenClubCommand = OpenLink("打开社团主页", "https://conduit-club.github.io/");
        ShowLiveLogsCommand = Command(() => Owner.SelectedPage = Owner.Logs);
        _presentationStore = new(DataDirectory);
        try { _savedPresentation = _presentationStore.Load(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { Feedback = "界面偏好未加载：" + UiPrivacy.Redact(ex.Message); }
        LoadPresentation(_savedPresentation);
        InitializeSourceEditor();
    }

    private AsyncRelayCommand OpenLink(string label, string url) => AsyncCommand(() => Owner.RunControlAsync(label, ct => Owner.ExternalLauncher.OpenUriAsync(new Uri(url), ct)));
    private void LoadPresentation(SettingsPresentationPreferences preferences)
    {
        var wasLoading = _loading; _loading = true;
        try { ShowClubIntroduction = preferences.ShowClubIntroduction; ShowClubServers = preferences.ShowClubServers; RotateHomeBackground = preferences.RotateHomeBackground; WindowOpacity = preferences.WindowOpacity; }
        finally { _loading = wasLoading; }
        ApplyPresentation();
    }
    private void ApplyPresentation() => Owner.Home.SetPresentation(_savedPresentation.ShowClubIntroduction, _savedPresentation.ShowClubServers, _savedPresentation.RotateHomeBackground, (double)_savedPresentation.WindowOpacity / 100);
    private async Task SavePresentationAsync(CancellationToken cancellationToken)
    {
        var preferences = new SettingsPresentationPreferences(ShowClubIntroduction, ShowClubServers, RotateHomeBackground, WindowOpacity);
        await _presentationStore.SaveAsync(preferences, cancellationToken);
        _savedPresentation = preferences;
        ApplyPresentation();
    }
    private bool ValidateLaunchDraft()
    {
        if (UseCustomWindow && (GameWindowWidth is < 320 or > 16384 || GameWindowHeight is < 320 or > 16384 || decimal.Truncate(GameWindowWidth) != GameWindowWidth || decimal.Truncate(GameWindowHeight) != GameWindowHeight))
        { Owner.ShowError("窗口宽高须为 320–16384 的整数。"); SelectedSection = "launch"; return false; }
        if (UseMinimumMemory && (MinimumMemoryMiB < 128 || MinimumMemoryMiB > MemoryMiB || decimal.Truncate(MinimumMemoryMiB) != MinimumMemoryMiB))
        { Owner.ShowError("初始内存须为不少于 128 MiB 且不超过最大内存的整数。"); SelectedSection = "launch"; return false; }
        if (WindowOpacity is < 50 or > 100) { Owner.ShowError("窗口不透明度须为 50–100%。"); SelectedSection = "appearance"; return false; }
        return true;
    }
    private void LoadLaunchDraft(LauncherSettings settings)
    {
        UseCustomWindow = settings.GameWindowWidth is not null || settings.GameWindowHeight is not null;
        GameWindowWidth = settings.GameWindowWidth ?? 854; GameWindowHeight = settings.GameWindowHeight ?? 480;
        GameFullscreen = settings.GameFullscreen; UseMinimumMemory = settings.MinimumMemoryMiB is not null;
        MinimumMemoryMiB = settings.MinimumMemoryMiB ?? 512; CustomInfo = settings.CustomInfo ?? "";
    }
    private void UpdateJava(IReadOnlyList<JavaInstallation> java)
    {
        var selected = SelectedJava?.ExecutablePath;
        if (!JavaInstallations.SequenceEqual(java)) { JavaInstallations.Clear(); foreach (var item in java) JavaInstallations.Add(item); }
        SelectedJava = JavaInstallations.FirstOrDefault(j => j.ExecutablePath == (selected ?? JavaExecutable));
        OnPropertyChanged(nameof(DiscoveredJavaTitle));
    }
    private void RefreshParity(LauncherSnapshot snapshot)
    {
        UpdateJava(snapshot.JavaInstallations);
        OnPropertyChanged(nameof(SoftwareName)); OnPropertyChanged(nameof(SoftwareVersion)); OnPropertyChanged(nameof(BuildDetails)); OnPropertyChanged(nameof(CredentialCapability));
    }
    private void RefreshLogFiles()
    {
        LogFiles.Clear();
        try
        {
            if (!Directory.Exists(LogsDirectory)) { LogFilesSummary = "尚无启动器日志文件。"; return; }
            foreach (var file in new DirectoryInfo(LogsDirectory).EnumerateFiles().Where(f => f.Extension is ".log" or ".txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(200))
                LogFiles.Add(new(UiPrivacy.Redact(file.Name), file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture), $"{file.Length / 1024d:0.#} KiB", UiPrivacy.Redact(file.FullName)));
            LogFilesSummary = LogFiles.Count == 0 ? "尚无启动器日志文件。" : $"显示最近 {LogFiles.Count} 个日志文件；导出前请在实时日志页检查脱敏内容。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LogFilesSummary = "无法读取日志目录：" + UiPrivacy.Redact(ex.Message); }
    }
}
