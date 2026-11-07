using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Tools;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record LicenseDocument(string Name, string Text);
public sealed partial class SettingsPageViewModel
{
    private SettingsPresentationPreferences appearanceOptions = new();
    private LauncherBehavior launcherOptions = new();
    private CancellationTokenSource? settingsWork;
    private TaskCompletionSource? settingsWorkDone;
    private string workSection = "";
    private bool autoJavaAttempted, autoFeedbackAttempted;
    private int navigationGeneration;
    private bool settingsBusy;
    private string launcherFeedback = "";
    private string downloadedUpdate = "";
    private string feedbackQuery = "";
    private IReadOnlyList<LauncherFeedback> allFeedback = [];
    private IReadOnlyList<HistoricalLauncherLog> historicalLogs = [];
    private string lastLogRecycle = "";
    private bool confirmLogCleanup;
    private readonly CancellationTokenSource settingsLifetime = new();
    public CancellationToken SettingsLifetime => settingsLifetime.Token;
    public void ShowLauncherFeedback(string text) => LauncherFeedback = text;
    public SettingsPresentationPreferences SavedPresentation => _savedPresentation;
    public Avalonia.Controls.GridLength SidebarWidth => new(Localization.UiLocalization.Instance.Locale.StartsWith("zh", StringComparison.Ordinal) ? 135 : 180);
    public bool IsLauncherBusy { get => settingsBusy; private set { if (SetProperty(ref settingsBusy, value)) NotifyCommands(); } }
    public string LauncherFeedback { get => launcherFeedback; private set => SetProperty(ref launcherFeedback, value); }
    public string BackgroundSummary => appearanceOptions.BackgroundImages is { Length: > 0 } images ? string.Join(Environment.NewLine, images.Select(Path.GetFileName)) : "当前使用内置社团实景。";
    public bool LockWindowSize { get => appearanceOptions.LockWindowSize; set => Appearance(value, p => p.LockWindowSize, (p, v) => p with { LockWindowSize = v }); }
    public bool ShowStartupLogo { get => appearanceOptions.ShowStartupLogo; set => Appearance(value, p => p.ShowStartupLogo, (p, v) => p with { ShowStartupLogo = v }); }
    public bool ShowLaunchTips { get => appearanceOptions.ShowLaunchTips; set => Appearance(value, p => p.ShowLaunchTips, (p, v) => p with { ShowLaunchTips = v }); }
    public bool NotifyClubUpdates { get => appearanceOptions.NotifyClubUpdates; set { Appearance(value, p => p.NotifyClubUpdates, (p, v) => p with { NotifyClubUpdates = v }); OnPropertyChanged(nameof(AnnouncementMode)); } }
    public IReadOnlyList<string> AnnouncementModes { get; } = ["off", "club-updates"];
    public string AnnouncementMode { get => NotifyClubUpdates ? "club-updates" : "off"; set => NotifyClubUpdates = value == "club-updates"; }
    public bool AdvancedMaterial { get => appearanceOptions.AdvancedMaterial; set => Appearance(value, p => p.AdvancedMaterial, (p, v) => p with { AdvancedMaterial = v }); }
    public string SelectedFont { get => appearanceOptions.FontFamily; set => Appearance(value ?? "", p => p.FontFamily, (p, v) => p with { FontFamily = v }); }
    public string LightAccent { get => appearanceOptions.LightAccent; set => Appearance(value, p => p.LightAccent, (p, v) => p with { LightAccent = v }); }
    public string DarkAccent { get => appearanceOptions.DarkAccent; set => Appearance(value, p => p.DarkAccent, (p, v) => p with { DarkAccent = v }); }
    public string TitleMode { get => appearanceOptions.TitleMode; set { Appearance(value, p => p.TitleMode, (p, v) => p with { TitleMode = v }); OnPropertyChanged(nameof(IsTextTitle)); OnPropertyChanged(nameof(IsImageTitle)); } }
    public bool IsTextTitle => TitleMode == "text";
    public bool IsImageTitle => TitleMode == "image";
    public string CustomTitleText { get => appearanceOptions.TitleText; set => Appearance(value, p => p.TitleText, (p, v) => p with { TitleText = v }); }
    public string CustomTitleImage { get => appearanceOptions.TitleImage; set => Appearance(value, p => p.TitleImage, (p, v) => p with { TitleImage = v }); }
    public string HomeSource { get => appearanceOptions.HomeSource; set { Appearance(value, p => p.HomeSource, (p, v) => p with { HomeSource = v }); OnPropertyChanged(nameof(IsCustomHome)); } }
    public bool IsCustomHome => HomeSource != "default";
    public string HomeLocation { get => appearanceOptions.HomeLocation; set => Appearance(value, p => p.HomeLocation, (p, v) => p with { HomeLocation = v }); }
    public bool HideDownloads { get => appearanceOptions.HiddenSections?.Contains("download") == true; set => SetHidden("download", value); }
    public bool HideTools { get => appearanceOptions.HiddenSections?.Contains("tools") == true; set => SetHidden("tools", value); }
    public string RegionalFormat { get => launcherOptions.RegionalFormat; set => Behavior(value, p => p.RegionalFormat, (p, v) => p with { RegionalFormat = v }); }
    public decimal LogLineLimit { get => launcherOptions.LogLineLimit; set => Behavior((int)value, p => p.LogLineLimit, (p, v) => p with { LogLineLimit = v }); }
    public bool DisableHardwareAcceleration { get => launcherOptions.DisableHardwareAcceleration; set => Behavior(value, p => p.DisableHardwareAcceleration, (p, v) => p with { DisableHardwareAcceleration = v }); }
    public bool UseDnsOverHttps { get => launcherOptions.UseDnsOverHttps; set => Behavior(value, p => p.UseDnsOverHttps, (p, v) => p with { UseDnsOverHttps = v }); }
    public bool CheckUpdatesOnStartup { get => launcherOptions.CheckUpdatesOnStartup; set => Behavior(value, p => p.CheckUpdatesOnStartup, (p, v) => p with { CheckUpdatesOnStartup = v }); }
    public IReadOnlyList<string> TitleModes { get; } = ["default", "none", "text", "image"];
    public IReadOnlyList<string> HomeSources { get; } = ["default", "local", "remote"];
    public IReadOnlyList<string> AccentChoices { get; } = ["default", "garnet", "blue", "jade"];
    public IReadOnlyList<string> RegionChoices { get; } = ["system", "zh-CN", "zh-TW", "en-US", "en-GB", "ja-JP", "fr-FR", "es-ES"];
    public ObservableCollection<string> FontChoices { get; } = [""];
    public ObservableCollection<LauncherRelease> Releases { get; } = [];
    public ObservableCollection<LauncherFeedback> FeedbackItems { get; } = [];
    public ObservableCollection<LicenseDocument> Licenses { get; } = [];
    private LauncherRelease? selectedRelease;
    public LauncherRelease? SelectedRelease { get => selectedRelease; set { if (SetProperty(ref selectedRelease, value)) { OnPropertyChanged(nameof(ReleaseNotes)); NotifyCommands(); } } }
    public string ReleaseNotes => SelectedRelease?.Notes ?? "";
    public string FeedbackQuery { get => feedbackQuery; set { if (SetProperty(ref feedbackQuery, value)) FilterFeedback(); } }
    public bool ConfirmLogCleanup { get => confirmLogCleanup; set { SetProperty(ref confirmLogCleanup, value); RecycleHistoricalLogsCommand.NotifyCanExecuteChanged(); } }
    public string HistoricalLogsSummary => historicalLogs.Count == 0 ? "没有可清理的历史日志。" : $"可回收 {historicalLogs.Count} 个超过七天的历史日志；当前日志保留。";
    public bool HasHistoricalLogPreview => historicalLogs.Count > 0 || Directory.Exists(lastLogRecycle);
    public AsyncRelayCommand CheckUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenDownloadedUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshFeedbackCommand { get; private set; } = null!;
    public AsyncRelayCommand<LauncherFeedback> OpenFeedbackItemCommand { get; private set; } = null!;
    public AsyncRelayCommand ScanHistoricalLogsCommand { get; private set; } = null!;
    public AsyncRelayCommand RecycleHistoricalLogsCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreHistoricalLogsCommand { get; private set; } = null!;
    public RelayCommand CancelSettingsWorkCommand { get; private set; } = null!;
    public RelayCommand RestoreBackgroundCommand { get; private set; } = null!;
    public RelayCommand RefreshBackgroundCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenPicturesDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenMusicDirectoryCommand { get; private set; } = null!;

    private void Appearance<T>(T value, Func<SettingsPresentationPreferences, T> get, Func<SettingsPresentationPreferences, T, SettingsPresentationPreferences> set, [CallerMemberName] string property = "")
    { if (EqualityComparer<T>.Default.Equals(get(appearanceOptions), value)) return; appearanceOptions = set(appearanceOptions, value); OnPropertyChanged(property); MarkDirty(); }
    private void Behavior<T>(T value, Func<LauncherBehavior, T> get, Func<LauncherBehavior, T, LauncherBehavior> set, [CallerMemberName] string property = "")
    { if (EqualityComparer<T>.Default.Equals(get(launcherOptions), value)) return; launcherOptions = set(launcherOptions, value); OnPropertyChanged(property); MarkDirty(); }
    private void SetHidden(string section, bool hidden, [CallerMemberName] string property = "")
    { var items = (appearanceOptions.HiddenSections ?? []).Where(s => s != section).ToList(); if (hidden) items.Add(section); appearanceOptions = appearanceOptions with { HiddenSections = items.ToArray() }; OnPropertyChanged(property); MarkDirty(); }
    public void SetBackgroundImages(IReadOnlyList<string> files)
    { appearanceOptions = appearanceOptions with { BackgroundImages = files.ToArray() }; OnPropertyChanged(nameof(BackgroundSummary)); MarkDirty(); }
    public void LoadFontChoices()
    { if (FontChoices.Count > 1) return; foreach (var font in FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().Order(StringComparer.CurrentCulture).Take(1000)) FontChoices.Add(font); }
    private void InitializeLauncherSettings()
    {
        RestoreBackgroundCommand = Command(() => SetBackgroundImages([]), () => CanEdit);
        RefreshBackgroundCommand = Command(() => { Owner.Home.RefreshBackgrounds(); LauncherFeedback = "背景已刷新。"; });
        OpenPicturesDirectoryCommand = OpenMediaDirectory("images"); OpenMusicDirectoryCommand = OpenMediaDirectory("music");
        CancelSettingsWorkCommand = Command(() => settingsWork?.Cancel(), () => IsLauncherBusy);
        CheckUpdatesCommand = AsyncCommand(() => RunSettingsWork(async ct =>
        {
            if (Service is not ILauncherInformationService info) throw new NotSupportedException("当前服务不支持版本检查。");
            var releases = await info.CheckLauncherReleasesAsync(ct); ct.ThrowIfCancellationRequested(); Releases.Clear(); foreach (var release in releases) Releases.Add(release);
            SelectedRelease = Releases.FirstOrDefault(r => !r.Prerelease) ?? Releases.FirstOrDefault();
            LauncherFeedback = releases.Count == 0 ? "没有已发布版本。" : SelectedRelease?.Package is null ? "已读取发行版本；暂无当前平台的可校验跨平台更新包。" : "已读取可用发行版本。";
        }), () => !IsLauncherBusy);
        DownloadUpdateCommand = AsyncCommand(() => RunSettingsWork(async ct =>
        { downloadedUpdate = await ((ILauncherInformationService)Service).DownloadLauncherUpdateAsync(SelectedRelease!.Package!, ct); LauncherFeedback = "更新包下载并校验完成。"; NotifyCommands(); }), () => !IsLauncherBusy && Service is ILauncherInformationService && SelectedRelease?.Package is not null);
        OpenDownloadedUpdateCommand = AsyncCommand(() => Owner.RunControlAsync("打开更新包目录", ct => Owner.ExternalLauncher.RevealFileAsync(downloadedUpdate, ct)), () => File.Exists(downloadedUpdate));
        RefreshFeedbackCommand = AsyncCommand(() => RunSettingsWork(async ct =>
        { autoFeedbackAttempted = true; var found = await ((ILauncherInformationService)Service).GetLauncherFeedbackAsync(ct); ct.ThrowIfCancellationRequested(); allFeedback = found; FilterFeedback(); LauncherFeedback = FeedbackItems.Count == 0 ? "没有匹配的反馈。" : ""; }), () => !IsLauncherBusy && Service is ILauncherInformationService);
        OpenFeedbackItemCommand = new(item => item is null ? Task.CompletedTask : Owner.RunControlAsync("打开反馈", ct => Owner.ExternalLauncher.OpenUriAsync(item.Page, ct)));
        ScanHistoricalLogsCommand = AsyncCommand(() => RunSettingsWork(async ct => { historicalLogs = await LauncherLogArchive.ScanAsync(LogsDirectory, ct); ConfirmLogCleanup = false; OnPropertyChanged(nameof(HistoricalLogsSummary)); OnPropertyChanged(nameof(HasHistoricalLogPreview)); if (historicalLogs.Count == 0) LauncherFeedback = HistoricalLogsSummary; }), () => !IsLauncherBusy);
        RecycleHistoricalLogsCommand = AsyncCommand(() => RunSettingsWork(async ct => { lastLogRecycle = await LauncherLogArchive.RecycleAsync(LogsDirectory, historicalLogs, ct); historicalLogs = []; ConfirmLogCleanup = false; OnPropertyChanged(nameof(HistoricalLogsSummary)); RefreshLogFiles(); LauncherFeedback = "历史日志已移入回收目录。"; NotifyCommands(); }), () => !IsLauncherBusy && ConfirmLogCleanup && historicalLogs.Count > 0);
        RestoreHistoricalLogsCommand = AsyncCommand(() => RunSettingsWork(async ct => { await LauncherLogArchive.RestoreAsync(LogsDirectory, lastLogRecycle, ct); RefreshLogFiles(); LauncherFeedback = "历史日志已恢复。"; }), () => !IsLauncherBusy && Directory.Exists(lastLogRecycle));
        LoadLicenses();
        InitializeMusic();
    }
    public Task AddJavaAsync(string path) => RunSettingsWork(async ct =>
    {
        if (Service is not IJavaManagementService management) { JavaExecutable = path; return; }
        var java = await management.InspectJavaAsync(path, ct);
        var existing = JavaInstallations.FirstOrDefault(j => j.ExecutablePath.Equals(java.ExecutablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null) JavaInstallations.Remove(existing);
        JavaInstallations.Insert(0, java); SelectedJava = java; OnPropertyChanged(nameof(DiscoveredJavaTitle));
        if (java.IsUsable) JavaExecutable = java.ExecutablePath; else LauncherFeedback = java.ProbeError ?? "无法识别此 Java。";
    });
    private AsyncRelayCommand OpenMediaDirectory(string kind) => AsyncCommand(() => Owner.RunControlAsync("打开素材目录", async ct =>
    { var path = SHOU.Launcher.Core.IO.SafePath.Within(DataDirectory, "appearance/" + kind); SHOU.Launcher.Core.IO.SafePath.RejectLinks(path); Directory.CreateDirectory(path); await Owner.ExternalLauncher.OpenDirectoryAsync(path, ct); }));
    private void LoadLicenses()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "licenses");
        var files = (Directory.Exists(root) ? new DirectoryInfo(root).EnumerateFiles() : [])
            .Concat(new[] { "LICENSE", "LICENSE.txt", "ThirdPartyNotices.txt", "THIRDPARTYNOTICES.txt" }.Select(n => new FileInfo(Path.Combine(AppContext.BaseDirectory, n))).Where(f => f.Exists));
        foreach (var file in files.Where(f => f.Length <= 1024 * 1024 && f.LinkTarget is null && f.Extension is ".txt" or ".md" or "").OrderBy(f => f.Name).DistinctBy(f => f.FullName).Take(100))
        { try { Licenses.Add(new(file.Name, File.ReadAllText(file.FullName))); } catch (IOException) { } }
    }
    private void FilterFeedback() { FeedbackItems.Clear(); foreach (var item in allFeedback.Where(f => f.Title.Contains(FeedbackQuery, StringComparison.CurrentCultureIgnoreCase) || f.Body.Contains(FeedbackQuery, StringComparison.CurrentCultureIgnoreCase))) FeedbackItems.Add(item); }
    private async Task RunSettingsWork(Func<CancellationToken, Task> work)
    {
        if (IsLauncherBusy || settingsLifetime.IsCancellationRequested) return; using var cts = CancellationTokenSource.CreateLinkedTokenSource(settingsLifetime.Token); cts.CancelAfter(TimeSpan.FromMinutes(5)); settingsWork = cts; var done = settingsWorkDone = new(TaskCreationOptions.RunContinuationsAsynchronously); workSection = SelectedSection; IsLauncherBusy = true; LauncherFeedback = "";
        try { await work(cts.Token); }
        catch (OperationCanceledException) { LauncherFeedback = "操作已取消或超时。"; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        { LauncherFeedback = UiPrivacy.Redact(ex.Message); }
        finally { settingsWork = null; IsLauncherBusy = false; done.TrySetResult(); if (ReferenceEquals(settingsWorkDone, done)) settingsWorkDone = null; }
    }
    private async Task NavigateLauncherSectionAsync(string section)
    {
        var generation = ++navigationGeneration;
        if (IsLauncherBusy && workSection != section) { settingsWork?.Cancel(); if (settingsWorkDone is { } pending) await pending.Task; }
        if (generation != navigationGeneration || settingsLifetime.IsCancellationRequested || SelectedSection != section) return;
        if (section == "java" && !autoJavaAttempted && JavaInstallations.Count == 0 && DiscoverJavaCommand.CanExecute(null)) await DiscoverJavaCommand.ExecuteAsync(null);
        if (section == "feedback" && !autoFeedbackAttempted && RefreshFeedbackCommand.CanExecute(null)) await RefreshFeedbackCommand.ExecuteAsync(null);
    }
    internal void OnNavigatingAway() => settingsWork?.Cancel();
    public async Task<bool> RunFileOperationAsync(Func<CancellationToken, Task> action)
    {
        var completed = false; await RunSettingsWork(async ct => { await action(ct); completed = true; }); return completed;
    }
    public async Task CancelAndWaitAsync() { var pending = settingsWorkDone?.Task; settingsWork?.Cancel(); if (pending is not null) await pending; }
    private void LoadLauncherOptions(LauncherSettings settings)
    { launcherOptions = settings.Launcher ?? new(); foreach (var name in new[] { nameof(RegionalFormat), nameof(LogLineLimit), nameof(DisableHardwareAcceleration), nameof(UseDnsOverHttps), nameof(CheckUpdatesOnStartup) }) OnPropertyChanged(name); }
    private void LoadAppearanceOptions(SettingsPresentationPreferences preferences)
    { appearanceOptions = preferences; foreach (var name in new[] { nameof(LockWindowSize), nameof(ShowStartupLogo), nameof(ShowLaunchTips), nameof(NotifyClubUpdates), nameof(AdvancedMaterial), nameof(SelectedFont), nameof(LightAccent), nameof(DarkAccent), nameof(TitleMode), nameof(CustomTitleText), nameof(CustomTitleImage), nameof(HomeSource), nameof(HomeLocation), nameof(IsCustomHome), nameof(IsImageTitle), nameof(IsTextTitle), nameof(HideDownloads), nameof(HideTools), nameof(BackgroundSummary) }) OnPropertyChanged(name); }
    private SettingsPresentationPreferences BuildPresentation() => appearanceOptions with { ShowClubIntroduction = ShowClubIntroduction, ShowClubServers = ShowClubServers, RotateHomeBackground = RotateHomeBackground, WindowOpacity = WindowOpacity, LastClubNotice = _savedPresentation.LastClubNotice };
    internal async Task RecordClubNoticeAsync(string key, CancellationToken ct)
    {
        var updated = _savedPresentation with { LastClubNotice = key };
        await _presentationStore.SaveAsync(updated, ct); _savedPresentation = updated;
    }
    public byte[] ExportSettings()
    {
        var saved = Owner.Snapshot.Settings;
        saved = saved with { MicrosoftClientId = null, JavaTrustSelection = null, Proxy = saved.Proxy is { } p ? p with { CredentialKey = null } : null, Aria2 = saved.Aria2 is { } a ? a with { CredentialKey = null } : null };
        if (System.Text.RegularExpressions.Regex.IsMatch(saved.DownloadSourcesJson, @"(?i)(token|password|secret|api[_-]?key|authorization)")) throw new InvalidDataException("自定义下载源含敏感字段，请先移除后导出。");
        return JsonSerializer.SerializeToUtf8Bytes(new { format = "SHOU-Settings-v1", settings = saved, presentation = _savedPresentation }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
    public void ImportSettings(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 1048576) throw new InvalidDataException("设置文件不能超过 1 MiB。");
        using var json = JsonDocument.Parse(data); if (json.RootElement.GetProperty("format").GetString() != "SHOU-Settings-v1") throw new InvalidDataException("请选择 SHOU 设置导出文件。");
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var imported = json.RootElement.GetProperty("settings").Deserialize<LauncherSettings>(opts) ?? throw new InvalidDataException("设置为空。");
        var presentation = json.RootElement.GetProperty("presentation").Deserialize<SettingsPresentationPreferences>(opts) ?? throw new InvalidDataException("界面偏好为空。");
        var current = Owner.Snapshot.Settings;
        imported = imported with { MicrosoftClientId = current.MicrosoftClientId, JavaTrustSelection = current.JavaTrustSelection,
            Proxy = imported.Proxy is { } p ? p with { CredentialKey = current.Proxy?.CredentialKey } : current.Proxy,
            Aria2 = imported.Aria2 is { } a ? a with { CredentialKey = current.Aria2?.CredentialKey } : current.Aria2 };
        SHOU.Launcher.Core.Configuration.ConfigurationStore.ValidateSettings(imported); SettingsPresentationStore.Validate(presentation);
        Load(imported); LoadPresentation(presentation); IsDirty = true; LauncherFeedback = "已读入设置草稿；点击保存设置后生效。";
    }
}
