using System.Text.Json;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class SettingsPageViewModel : PageViewModel
{
    private bool _loading;
    private bool _loaded;
    private bool _isDirty;
    private string _gameRoot = "";
    private string _javaExecutable = "";
    private decimal _memoryMiB = 2048;
    private string _language = "zh-CN";
    private string _theme = "System";
    private string _downloadSourcesJson = "";
    private ProxyMode _proxyMode = ProxyMode.System;
    private string _proxyAddress = "";
    private string _proxySecret = "";
    private bool _aria2Enabled;
    private string _aria2Endpoint = "http://localhost:6800/jsonrpc";
    private string _aria2Secret = "";
    private string _microsoftClientId = "";
    private string _feedback = "";
    public string GameRoot { get => _gameRoot; set { if (SetProperty(ref _gameRoot, value)) MarkDirty(); } }
    public string JavaExecutable { get => _javaExecutable; set { if (SetProperty(ref _javaExecutable, value)) MarkDirty(); } }
    public decimal MemoryMiB { get => _memoryMiB; set { if (SetProperty(ref _memoryMiB, value)) MarkDirty(); } }
    public string Language { get => _language; set { if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _language, value)) MarkDirty(); } }
    public string Theme { get => _theme; set { if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _theme, value)) MarkDirty(); } }
    public string DownloadSourcesJson { get => _downloadSourcesJson; set { if (SetProperty(ref _downloadSourcesJson, value)) MarkDirty(); } }
    public ProxyMode ProxyMode { get => _proxyMode; set { if (SetProperty(ref _proxyMode, value)) { MarkDirty(); OnPropertyChanged(nameof(IsCustomProxy)); } } }
    public bool IsCustomProxy => ProxyMode == ProxyMode.Custom;
    public string ProxyAddress { get => _proxyAddress; set { if (SetProperty(ref _proxyAddress, value)) MarkDirty(); } }
    public string ProxySecret { get => _proxySecret; set { if (SetProperty(ref _proxySecret, value)) MarkDirty(); } }
    public bool Aria2Enabled { get => _aria2Enabled; set { if (SetProperty(ref _aria2Enabled, value)) MarkDirty(); } }
    public string Aria2Endpoint { get => _aria2Endpoint; set { if (SetProperty(ref _aria2Endpoint, value)) MarkDirty(); } }
    public string Aria2Secret { get => _aria2Secret; set { if (SetProperty(ref _aria2Secret, value)) MarkDirty(); } }
    public string MicrosoftClientId { get => _microsoftClientId; set { if (SetProperty(ref _microsoftClientId, value)) MarkDirty(); } }
    public bool IsDirty { get => _isDirty; private set { SetProperty(ref _isDirty, value); OnPropertyChanged(nameof(SaveSummary)); NotifyCommands(); } }
    public string SaveSummary => IsDirty ? "有未保存的修改；切换页面会保留草稿。" : "当前显示已保存的设置。";
    public string Feedback { get => _feedback; private set => SetProperty(ref _feedback, value); }
    public string DataDirectory => Service.Paths.DataDirectory;
    public string CacheDirectory => Service.Paths.CacheDirectory;
    public string LogsDirectory => Service.Paths.LogsDirectory;
    public string CredentialsSummary => Owner.Snapshot.CanPersistAccounts ? "密钥写入系统安全存储；留空保留已有密钥。" : "安全存储不可用，不能保存新的连接密钥。";
    public ObservableCollection<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues<ProxyMode>();
    public ObservableCollection<string> Languages { get; } = ["zh-CN", "en-US", "system"];
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand DiscardDraftCommand { get; }
    public AsyncRelayCommand OpenDataDirectoryCommand { get; }

    public SettingsPageViewModel(MainWindowViewModel owner) : base(owner, "设置", "游戏路径、运行时、界面与网络配置。保存后用于后续操作。")
    {
        SaveCommand = AsyncCommand(SaveAsync, () => CanEdit && IsDirty);
        DiscardDraftCommand = Command(() => { Load(Owner.Snapshot.Settings); Feedback = "已恢复为已保存的设置。"; }, () => CanEdit && IsDirty);
        OpenDataDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开数据目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(DataDirectory, ct)));
    }
    private void MarkDirty() { if (!_loading) { IsDirty = true; Feedback = ""; } }
    private async Task SaveAsync()
    {
        var gameRoot = GameRoot.Trim();
        var java = JavaExecutable.Trim();
        if ((gameRoot.Length > 0 && !Path.IsPathFullyQualified(gameRoot)) || (java.Length > 0 && !Path.IsPathFullyQualified(java))) { Owner.ShowError("游戏与 Java 路径须为绝对路径；留空使用默认选择。"); return; }
        if (MemoryMiB is < 512 or > 65536 || decimal.Truncate(MemoryMiB) != MemoryMiB) { Owner.ShowError("内存须为 512–65536 MiB 的整数。"); return; }
        if (!string.IsNullOrWhiteSpace(MicrosoftClientId) && !Guid.TryParse(MicrosoftClientId.Trim(), out _)) { Owner.ShowError("Microsoft Client ID 必须是有效 GUID。"); return; }
        try { if (!string.IsNullOrWhiteSpace(DownloadSourcesJson)) using (JsonDocument.Parse(DownloadSourcesJson)) { } }
        catch (JsonException) { Owner.ShowError("下载源 JSON 格式无效；请修正后保存。"); return; }
        if (IsCustomProxy && !IsHttpEndpoint(ProxyAddress)) { Owner.ShowError("自定义代理需要有效的 HTTP/HTTPS 地址；凭据请填入独立密钥字段。"); return; }
        if (Aria2Enabled && !IsHttpEndpoint(Aria2Endpoint)) { Owner.ShowError("aria2 RPC 地址需要有效的 HTTP/HTTPS URL，不能包含明文凭据。"); return; }
        if (ProxyMode == ProxyMode.System && !Service.Capabilities.SupportsSystemProxy) { Owner.ShowError("当前平台尚不支持系统代理，请明确选择直连或自定义代理。"); return; }
        var proxySecret = ProxySecret;
        var ariaSecret = Aria2Secret;
        ProxySecret = ""; Aria2Secret = "";
        var saved = await Owner.RunAsync("保存设置", async ct =>
        {
            if (ariaSecret.Length > 0) await Service.SaveConnectionSecretAsync("aria2", ariaSecret, ct);
            if (proxySecret.Length > 0) await Service.SaveConnectionSecretAsync("proxy", proxySecret, ct);
            var latest = Service.Snapshot.Settings;
            var settings = new LauncherSettings(gameRoot.Length == 0 ? null : gameRoot, java.Length == 0 ? null : java,
                (int)MemoryMiB, Language, Theme, DownloadSourcesJson,
                new ProxySettings(ProxyMode, string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(), latest.Proxy?.CredentialKey),
                new Aria2Settings(Aria2Enabled, Aria2Endpoint.Trim(), latest.Aria2?.CredentialKey),
                string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim());
            await Service.SaveSettingsAsync(settings, ct);
        }, false, proxySecret, ariaSecret);
        if (saved) { Load(Service.Snapshot.Settings); Feedback = "设置已保存。"; }
        else Feedback = "保存未完成，请查看错误提示。已有密钥不会以明文保存或被空值覆盖。";
    }
    private static bool IsHttpEndpoint(string address) => Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private void Load(LauncherSettings settings)
    {
        _loading = true;
        try
        {
            GameRoot = settings.GameRoot ?? ""; JavaExecutable = settings.JavaExecutable ?? ""; MemoryMiB = settings.MemoryMiB;
            if (!string.IsNullOrWhiteSpace(settings.Language) && !Languages.Contains(settings.Language)) Languages.Add(settings.Language);
            if (!string.IsNullOrWhiteSpace(settings.Theme) && !Themes.Contains(settings.Theme)) Themes.Add(settings.Theme);
            Language = settings.Language; Theme = settings.Theme; DownloadSourcesJson = settings.DownloadSourcesJson;
            ProxyMode = settings.Proxy?.Mode ?? ProxyMode.System; ProxyAddress = settings.Proxy?.Address ?? "";
            Aria2Enabled = settings.Aria2?.Enabled ?? false; Aria2Endpoint = settings.Aria2?.Endpoint ?? "http://localhost:6800/jsonrpc";
            MicrosoftClientId = settings.MicrosoftClientId ?? ""; ProxySecret = ""; Aria2Secret = "";
            IsDirty = false; _loaded = true;
        }
        finally { _loading = false; }
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        if (!_loaded || !IsDirty) Load(snapshot.Settings);
        OnPropertyChanged(nameof(CredentialsSummary));
        base.Refresh(snapshot);
    }
}
