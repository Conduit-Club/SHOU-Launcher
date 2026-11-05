using System.Text.Json;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record JavaTrustFindingViewModel(string Path, string Hash, string Summary, string Details);

public sealed partial class SettingsPageViewModel : PageViewModel
{
    private bool _loading;
    private bool _loaded;
    private bool _isDirty;
    private string _gameRoot = "";
    private string _javaExecutable = "";
    private bool _conduitJavaTrustAutoRepair = true;
    private long _javaTrustSelectionGeneration;
    private string _javaTrustStatus = "尚未扫描。请明确选择一个 Java 可执行文件。";
    private string _javaTrustProvenance = "";
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
    public string JavaExecutable
    {
        get => _javaExecutable;
        set
        {
            if (!SetProperty(ref _javaExecutable, value)) return;
            ++_javaTrustSelectionGeneration;
            JavaTrustFindings.Clear(); JavaTrustProvenance = "";
            JavaTrustStatus = "尚未扫描当前 Java。请先只读扫描，或创建独立修复副本。";
            OnPropertyChanged(nameof(HasExplicitJava));
            OnPropertyChanged(nameof(JavaSelectionSummary));
            MarkDirty(); NotifyCommands();
        }
    }
    public bool ConduitJavaTrustAutoRepair { get => _conduitJavaTrustAutoRepair; set { if (SetProperty(ref _conduitJavaTrustAutoRepair, value)) MarkDirty(); } }
    public bool HasExplicitJava => !string.IsNullOrWhiteSpace(JavaExecutable) && Path.IsPathFullyQualified(JavaExecutable.Trim());
    public ObservableCollection<JavaTrustFindingViewModel> JavaTrustFindings { get; } = [];
    public string JavaTrustStatus { get => _javaTrustStatus; private set => SetProperty(ref _javaTrustStatus, value); }
    public string JavaTrustProvenance { get => _javaTrustProvenance; private set { SetProperty(ref _javaTrustProvenance, value); OnPropertyChanged(nameof(HasJavaTrustProvenance)); } }
    public bool HasJavaTrustProvenance => JavaTrustProvenance.Length > 0;
    public decimal MemoryMiB { get => _memoryMiB; set { if (SetProperty(ref _memoryMiB, value)) { MarkDirty(); OnPropertyChanged(nameof(MemorySummary)); } } }
    public string Language { get => _language; set { if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _language, value)) MarkDirty(); } }
    public string Theme { get => _theme; set { if (!string.IsNullOrWhiteSpace(value) && SetProperty(ref _theme, value)) MarkDirty(); } }
    public string DownloadSourcesJson { get => _downloadSourcesJson; set { if (SetProperty(ref _downloadSourcesJson, value)) { MarkDirty(); RefreshSourceEditor(value); } } }
    public ProxyMode ProxyMode { get => _proxyMode; set { if (SetProperty(ref _proxyMode, value)) { MarkDirty(); OnPropertyChanged(nameof(IsCustomProxy)); } } }
    public bool IsCustomProxy => ProxyMode == ProxyMode.Custom;
    public string ProxyAddress { get => _proxyAddress; set { if (SetProperty(ref _proxyAddress, value)) MarkDirty(); } }
    public string ProxySecret { get => _proxySecret; set { if (SetProperty(ref _proxySecret, value)) MarkDirty(); } }
    public bool Aria2Enabled { get => _aria2Enabled; set { if (SetProperty(ref _aria2Enabled, value)) { if (value) Aria2DetailsExpanded = true; MarkDirty(); } } }
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
    public AsyncRelayCommand InspectJavaTrustCommand { get; }
    public AsyncRelayCommand CreateRepairedJavaCopyCommand { get; }

    public SettingsPageViewModel(MainWindowViewModel owner) : base(owner, "设置", "游戏路径、运行时、界面与网络配置。保存后用于后续操作。")
    {
        SaveCommand = AsyncCommand(SaveAsync, () => CanEdit && IsDirty);
        DiscardDraftCommand = Command(() => { Load(Owner.Snapshot.Settings); LoadPresentation(_savedPresentation); Feedback = "已恢复为已保存的设置。"; }, () => CanEdit && IsDirty);
        OpenDataDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开数据目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(DataDirectory, ct)));
        InspectJavaTrustCommand = AsyncCommand(InspectJavaTrustAsync, () => Owner.CanReadRemote && HasExplicitJava);
        CreateRepairedJavaCopyCommand = AsyncCommand(CreateRepairedJavaCopyAsync, () => CanEdit && HasExplicitJava);
        InitializeParity();
    }
    private void MarkDirty() { if (!_loading) { IsDirty = true; Feedback = ""; } }
    private async Task SaveAsync()
    {
        var gameRoot = GameRoot.Trim();
        var java = JavaExecutable.Trim();
        if ((gameRoot.Length > 0 && !Path.IsPathFullyQualified(gameRoot)) || (java.Length > 0 && !Path.IsPathFullyQualified(java))) { Owner.ShowError("游戏与 Java 路径须为绝对路径；留空使用默认选择。"); return; }
        if (MemoryMiB is < 512 or > 32768 || decimal.Truncate(MemoryMiB) != MemoryMiB) { Owner.ShowError("内存须为 512–32768 MiB 的整数。"); return; }
        if (!ValidateLaunchDraft()) return;
        if (!string.IsNullOrWhiteSpace(MicrosoftClientId) && !Guid.TryParse(MicrosoftClientId.Trim(), out _)) { Owner.ShowError("Microsoft Client ID 必须是有效 GUID。"); return; }
        try { _ = PCL.Core.Conduit.DownloadSources.Parse(DownloadSourcesJson); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { Owner.ShowError("下载源配置无效：" + UiPrivacy.Redact(ex.Message)); SelectedSection = "manage"; return; }
        if (IsCustomProxy && !IsHttpEndpoint(ProxyAddress)) { Owner.ShowError("自定义代理需要有效的 HTTP/HTTPS 地址；凭据请填入独立密钥字段。"); return; }
        if (ProxySecret.Length > 0 && !ProxySecret.Contains(':')) { Owner.ShowError("代理凭据须使用 username:password 格式，不会以明文保存。"); return; }
        if (Aria2Enabled && !IsLocalAria2Endpoint(Aria2Endpoint)) { Owner.ShowError("本候选仅支持本机 aria2 RPC（localhost、127.0.0.1 或 ::1），尚不支持远程 RPC；地址须为不含凭据、查询或片段的 HTTP/HTTPS URL。"); return; }
        if (ProxyMode == ProxyMode.System && !Service.Capabilities.SupportsSystemProxy) { Owner.ShowError("当前平台尚不支持系统代理，请明确选择直连或自定义代理。"); return; }
        var proxySecret = ProxySecret;
        var ariaSecret = Aria2Secret;
        ProxySecret = ""; Aria2Secret = "";
        var saved = await Owner.RunAsync("保存设置", async ct =>
        {
            if (ariaSecret.Length > 0) await Service.SaveConnectionSecretAsync("aria2", ariaSecret, ct);
            if (proxySecret.Length > 0) await Service.SaveConnectionSecretAsync("proxy", proxySecret, ct);
            var latest = Service.Snapshot.Settings;
            var settings = latest with { GameRoot = gameRoot.Length == 0 ? null : gameRoot, JavaExecutable = java.Length == 0 ? null : java,
                MemoryMiB = (int)MemoryMiB, Language = Language, Theme = Theme, DownloadSourcesJson = DownloadSourcesJson,
                Proxy = new ProxySettings(ProxyMode, string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(), latest.Proxy?.CredentialKey),
                Aria2 = new Aria2Settings(Aria2Enabled, Aria2Endpoint.Trim(), latest.Aria2?.CredentialKey),
                MicrosoftClientId = string.IsNullOrWhiteSpace(MicrosoftClientId) ? null : MicrosoftClientId.Trim(), ConduitJavaTrustAutoRepair = ConduitJavaTrustAutoRepair,
                GameWindowWidth = UseCustomWindow ? (int)GameWindowWidth : null, GameWindowHeight = UseCustomWindow ? (int)GameWindowHeight : null,
                GameFullscreen = GameFullscreen, MinimumMemoryMiB = UseMinimumMemory ? (int)MinimumMemoryMiB : null,
                CustomInfo = string.IsNullOrWhiteSpace(CustomInfo) ? null : CustomInfo.Trim() };
            await Service.SaveSettingsAsync(settings, ct);
            await SavePresentationAsync(ct);
        }, false, proxySecret, ariaSecret);
        if (saved) { Load(Service.Snapshot.Settings); Feedback = "设置已保存。"; }
        else Feedback = "保存未完成，请查看错误提示；部分已成功写入的项目可能已生效。已有密钥不会被空值覆盖。";
    }
    private static bool IsHttpEndpoint(string address) => Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private static bool IsLocalAria2Endpoint(string address) => Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    private bool TryGetTrustJava(out string path)
    {
        path = JavaExecutable.Trim();
        if (path.Length > 0 && Path.IsPathFullyQualified(path)) return true;
        JavaTrustStatus = "未开始：请先填写所选 Java 可执行文件的绝对路径；留空的自动选择不能作为扫描或修复目标。";
        Owner.ShowError("Java 信任操作需要明确的 Java 绝对路径。");
        return false;
    }
    private async Task InspectJavaTrustAsync()
    {
        if (!TryGetTrustJava(out var path)) return;
        var generation = _javaTrustSelectionGeneration;
        JavaTrustFindings.Clear(); JavaTrustStatus = "正在只读扫描所选 Java 的信任配置…";
        IReadOnlyList<JavaTrustFinding>? findings = null;
        var completed = await Owner.RunAsync("只读扫描 Java 信任配置", async ct =>
        {
            findings = await Service.InspectJavaTrustAsync(path, ct);
            ct.ThrowIfCancellationRequested();
        }, true);
        if (generation != _javaTrustSelectionGeneration) return;
        if (!completed) { JavaTrustStatus = "扫描失败或已取消，无法据此确认信任配置；请查看错误或重试。"; return; }
        ShowTrustFindings(findings ?? []);
        JavaTrustStatus = findings is not { Count: > 0 } ? "扫描未返回检查项，无法据此确认信任配置。"
            : findings.Any(f => !f.CanRepair) ? $"只读扫描返回 {findings.Count} 项；存在未能完整检查或当前策略不支持的条目，无法确认信任配置。"
            : $"只读扫描完成，共 {findings.Count} 项；{(findings.Any(f => f.NeedsRepair) ? "存在需检查 / 修复项，可创建独立副本。" : "未报告需修复项。结果仅针对所选 Java 的信任配置。")}";
    }
    private async Task CreateRepairedJavaCopyAsync()
    {
        if (!TryGetTrustJava(out var path)) return;
        var hadDraft = IsDirty;
        JavaTrustFindings.Clear(); JavaTrustProvenance = "";
        JavaTrustStatus = "正在创建独立修复副本；原始 Java 不会被修改…";
        JavaTrustDerivation? derivation = null;
        var completed = await Owner.RunAsync("创建 Java 修复副本并选用", async ct =>
        {
            derivation = await Service.CreateRepairedJavaCopyAsync(path, ct);
            if (!derivation.Java.IsUsable || !Path.IsPathFullyQualified(derivation.Java.ExecutablePath))
                throw new InvalidDataException("服务未返回通过运行时探测的 Java 修复副本。");
        });
        if (!completed || derivation is null)
        {
            JavaTrustStatus = "创建失败或已取消，未选用新副本；请查看错误或重试。";
            return;
        }
        if (hadDraft) JavaExecutable = derivation.Java.ExecutablePath;
        else Load(Service.Snapshot.Settings);
        ShowTrustFindings(derivation.Findings);
        JavaTrustProvenance = UiPrivacy.Redact($"策略：{derivation.PolicyId}\n原始 Java：{derivation.SourceJavaExecutable}\n选用副本：{derivation.Java.ExecutablePath}\n来源树 SHA-256：{derivation.SourceTreeSha256}\n副本树 SHA-256：{derivation.OutputTreeSha256}\n来源记录：{derivation.ProvenancePath}");
        JavaTrustStatus = "已创建并选用通过运行时探测的独立修复副本，Java 设置已保存；原始 Java 未修改。";
        Feedback = hadDraft ? "修复副本已选用；其余未保存设置仍保留在草稿中。" : "修复副本已创建，新的全局 Java 路径已保存。";
    }
    private void ShowTrustFindings(IReadOnlyList<JavaTrustFinding> findings)
    {
        JavaTrustFindings.Clear();
        if (findings.Count > 0) JavaTrustDetailsExpanded = true;
        foreach (var finding in findings)
            JavaTrustFindings.Add(new(UiPrivacy.Redact(finding.Path), UiPrivacy.Redact(finding.Hash),
                (finding.IsTrustStore ? "证书信任库 · " : "信任配置 · ") + (finding.NeedsRepair ? finding.CanRepair ? "可在独立副本中修复" : "需检查，当前策略无法修复" : finding.CanRepair ? "未报告需修复项" : "未能检查，结果未知"),
                UiPrivacy.Redact(finding.Details)));
    }
    private void Load(LauncherSettings settings)
    {
        _loading = true;
        try
        {
            GameRoot = settings.GameRoot ?? ""; JavaExecutable = settings.JavaExecutable ?? ""; MemoryMiB = settings.MemoryMiB;
            ConduitJavaTrustAutoRepair = settings.ConduitJavaTrustAutoRepair;
            if (!string.IsNullOrWhiteSpace(settings.Language) && !Languages.Contains(settings.Language)) Languages.Add(settings.Language);
            if (!string.IsNullOrWhiteSpace(settings.Theme) && !Themes.Contains(settings.Theme)) Themes.Add(settings.Theme);
            Language = settings.Language; Theme = settings.Theme; DownloadSourcesJson = settings.DownloadSourcesJson;
            ProxyMode = settings.Proxy?.Mode ?? ProxyMode.System; ProxyAddress = settings.Proxy?.Address ?? "";
            Aria2Enabled = settings.Aria2?.Enabled ?? false; Aria2Endpoint = settings.Aria2?.Endpoint ?? "http://localhost:6800/jsonrpc";
            MicrosoftClientId = settings.MicrosoftClientId ?? ""; ProxySecret = ""; Aria2Secret = "";
            LoadLaunchDraft(settings);
            RefreshSourceEditor(settings.DownloadSourcesJson);
            IsDirty = false; _loaded = true;
        }
        finally { _loading = false; }
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        if (!_loaded || !IsDirty) Load(snapshot.Settings);
        OnPropertyChanged(nameof(CredentialsSummary));
        RefreshParity(snapshot);
        base.Refresh(snapshot);
    }
}
