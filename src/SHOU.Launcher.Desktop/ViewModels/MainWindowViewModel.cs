using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public abstract class PageViewModel(MainWindowViewModel owner, string title, string description) : ObservableObject
{
    protected MainWindowViewModel Owner { get; } = owner;
    public MainWindowViewModel Root => Owner;
    protected ILauncherService Service => Owner.Service;
    public string Title => Localization.UiLocalization.Instance.Text(title);
    public string Description => Localization.UiLocalization.Instance.Text(description);
    private readonly List<IRelayCommand> _commands = [];
    protected AsyncRelayCommand AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        var command = canExecute is null ? new AsyncRelayCommand(execute) : new AsyncRelayCommand(execute, canExecute);
        _commands.Add(command);
        return command;
    }
    protected RelayCommand Command(Action execute, Func<bool>? canExecute = null)
    {
        var command = canExecute is null ? new RelayCommand(execute) : new RelayCommand(execute, canExecute);
        _commands.Add(command);
        return command;
    }
    public virtual void Refresh(LauncherSnapshot snapshot) => NotifyCommands();
    public virtual void NotifyCommands()
    {
        foreach (var command in _commands) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
    }
    public bool CanEdit => Owner.CanMutate;
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    internal ILauncherService Service { get; }
    public IExternalLauncher ExternalLauncher { get; }
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _executing;
    private bool closing;
    private TaskCompletionSource? operationCompletion;
    private bool _disposed;
    private bool _initialized;
    private string?[] _sensitiveValues = [];
    private string _operationLabel = "就绪";
    private string? _errorMessage;
    private PageViewModel _selectedPage = null!;
    public LauncherSnapshot Snapshot { get; private set; }
    public HomePageViewModel Home { get; }
    public InstancesPageViewModel Instances { get; }
    public DownloadsPageViewModel Downloads { get; }
    public AccountsPageViewModel Accounts { get; }
    public SettingsPageViewModel Settings { get; }
    public LogsPageViewModel Logs { get; }
    public ToolsPageViewModel Tools { get; }
    public ObservableCollection<PageViewModel> Pages { get; }
    public RelayCommand ShowHomeCommand { get; }
    public RelayCommand ShowDownloadsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowToolsCommand { get; }
    public RelayCommand ShowInstancesCommand { get; }
    public RelayCommand ShowAccountsCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public AsyncRelayCommand StopGameCommand { get; }

    public MainWindowViewModel(ILauncherService service, IExternalLauncher externalLauncher, ILauncherCatalogService? catalog = null)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        ExternalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        Snapshot = service.Snapshot;
        Home = new(this);
        Instances = new(this);
        Downloads = new(this, catalog);
        InitializeTaskNotifications();
        Accounts = new(this);
        Settings = new(this);
        Logs = new(this);
        Tools = new(this);
        Pages = [Home, Instances, Downloads, Accounts, Settings, Logs, Tools];
        _selectedPage = Home;
        ShowHomeCommand = new(() => SelectedPage = Home);
        ShowDownloadsCommand = new(() => SelectedPage = Downloads);
        ShowSettingsCommand = new(() => SelectedPage = Settings);
        ShowToolsCommand = new(() => SelectedPage = Tools);
        ShowInstancesCommand = new(() => Instances.ShowSelectionCommand.Execute(null));
        ShowAccountsCommand = new(() => SelectedPage = Accounts);
        ShowLogsCommand = new(() => SelectedPage = Logs);
        BackCommand = new(() =>
        {
            if (ReferenceEquals(SelectedPage, Instances)) Instances.NavigateBackCommand.Execute(null);
            else if (ReferenceEquals(SelectedPage, Downloads) && Downloads.IsResourceDetail) Downloads.BackToListCommand.Execute(null);
            else if (ReferenceEquals(SelectedPage, Downloads) && Downloads.IsTasksPage) Downloads.SelectCategory("modpack");
            else SelectedPage = ReferenceEquals(SelectedPage, Logs) ? Settings : Home;
        }, () => IsContextPage);
        CancelOperationCommand = new(CancelCurrentOperation, () => _operation is { IsCancellationRequested: false });
        DismissErrorCommand = new(() => ShowError(null));
        StopGameCommand = new(() => RunControlAsync("停止游戏", ct => Service.StopGameAsync(ct)), () => HasRunningGame);
        Service.SnapshotChanged += OnSnapshotChanged;
        Service.LogReceived += OnLogReceived;
        Instances.PropertyChanged += OnInstancesPropertyChanged;
        Downloads.PropertyChanged += OnDownloadsPropertyChanged;
        ApplySnapshot(Snapshot);
    }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null) return;
            if (ReferenceEquals(value, _selectedPage)) { NotifyNavigation(); if (ReferenceEquals(value, Downloads)) Downloads.OnNavigatedTo(); return; }
            var leavingAccounts = ReferenceEquals(_selectedPage, Accounts) || ReferenceEquals(_selectedPage, Home);
            var leavingDownloads = ReferenceEquals(_selectedPage, Downloads);
            var leavingTools = ReferenceEquals(_selectedPage, Tools);
            var leavingSettings = ReferenceEquals(_selectedPage, Settings);
            if (!SetProperty(ref _selectedPage, value)) return;
            if (leavingAccounts) Accounts.OnNavigatingAway();
            if (leavingDownloads) Downloads.OnNavigatingAway();
            if (leavingTools) Tools.OnNavigatingAway();
            if (leavingSettings) Settings.OnNavigatingAway();
            NotifyNavigation();
            if (ReferenceEquals(value, Downloads)) Downloads.OnNavigatedTo();
        }
    }
    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(IsContextPage));
        OnPropertyChanged(nameof(NavigationTitle));
        OnPropertyChanged(nameof(IsLaunchSelected));
        OnPropertyChanged(nameof(IsDownloadsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(ShowOperationStatus));
        OnPropertyChanged(nameof(OperationInset));
        BackCommand.NotifyCanExecuteChanged();
    }
    public string CurrentSection => ReferenceEquals(SelectedPage, Downloads) ? "download"
        : ReferenceEquals(SelectedPage, Settings) || ReferenceEquals(SelectedPage, Logs) ? "settings"
        : ReferenceEquals(SelectedPage, Tools) ? "tools" : "launch";
    public bool IsContextPage => ReferenceEquals(SelectedPage, Instances) || ReferenceEquals(SelectedPage, Accounts) || ReferenceEquals(SelectedPage, Logs)
        || (ReferenceEquals(SelectedPage, Downloads) && (Downloads.IsResourceDetail || Downloads.IsTasksPage));
    public string NavigationTitle => ReferenceEquals(SelectedPage, Instances) ? (Instances.IsWorldDetails ? Instances.WorldContextTitle : Instances.ContextTitle)
        : ReferenceEquals(SelectedPage, Downloads) && Downloads.IsTasksPage ? "任务管理"
        : ReferenceEquals(SelectedPage, Downloads) && Downloads.IsResourceDetail ? "资源下载 - " + Downloads.ProjectTitle : SelectedPage.Title;
    public bool IsLaunchSelected => CurrentSection == "launch";
    public bool IsDownloadsSelected => CurrentSection == "download";
    public bool IsSettingsSelected => CurrentSection == "settings";
    public bool IsToolsSelected => CurrentSection == "tools";
    public string WindowTitle => Localization.UiLocalization.Instance.Text(Snapshot.Build.Name);
    public string BrandTitle => Settings.SavedPresentation.TitleMode == "text" ? Settings.SavedPresentation.TitleText : WindowTitle;
    internal void NotifyLauncherAppearance() => OnPropertyChanged(nameof(BrandTitle));
    public string BuildSummary => $"{Snapshot.Build.Version} · {Snapshot.Build.Codename} · {Snapshot.Build.Configuration}";
    public string SourceSummary => Snapshot.Build.SourceSha.Length > 12 ? Snapshot.Build.SourceSha[..12] : Snapshot.Build.SourceSha;
    public string PlatformSummary => $"{Snapshot.Platform.OS} · 硬件 {Snapshot.Platform.HardwareArchitecture} / 进程 {Snapshot.Platform.ProcessArchitecture}";
    public string StateSummary => _executing ? _operationLabel : Snapshot.State switch
    {
        "Installing" => "正在安装", "Launching" => "正在启动", "Running" => "就绪",
        "Authenticating" => "正在验证账户", "Error" => "操作失败，可修正后重试", _ => _operationLabel
    };
    public bool IsBusy => _executing || Snapshot.State is "Installing" or "Launching" or "Authenticating";
    public bool CanMutate => !_disposed && !closing && !_executing && Snapshot.State is "Ready" or "Error" or "Running";
    public bool CanReadRemote => !_disposed && !closing && !_executing && Snapshot.State is "Ready" or "Error" or "Running";
    public bool HasRunningGame => !_disposed && (Snapshot.State == "Running" || Snapshot.GameProcessId is not null);
    private int inlineOperations;
    public string OperationTransferSummary
    {
        get
        {
            if (!IsBusy) return "";
            var transfer = Snapshot.Downloads.LastOrDefault(t => t.State is "Downloading" or "Queued" or "Verifying" or "Retrying");
            if (transfer is null) return "";
            var bytes = transfer.TotalBytes is > 0 ? $"{transfer.BytesReceived / 1048576d:F1} / {transfer.TotalBytes.Value / 1048576d:F1} MiB" : $"{transfer.BytesReceived / 1048576d:F1} MiB";
            return $"当前文件：{UiPrivacy.Redact(transfer.Name)}\n{bytes}";
        }
    }
    public bool HasOperationTransferSummary => OperationTransferSummary.Length > 0;
    public bool ShowOperationStatus => !backgroundInstall && inlineOperations == 0 && IsBusy && (!IsHomeLaunching || !ReferenceEquals(SelectedPage, Home));
    public async Task<bool> RunInlineAsync(string label, Func<CancellationToken, Task> action)
    {
        inlineOperations++; OnPropertyChanged(nameof(ShowOperationStatus));
        try { return await RunAsync(label, action); }
        finally { inlineOperations--; OnPropertyChanged(nameof(ShowOperationStatus)); }
    }
    public Thickness OperationInset => new(ReferenceEquals(SelectedPage, Downloads) ? Downloads.ShowCategorySidebar ? 180 : 0
        : ReferenceEquals(SelectedPage, Settings) || ReferenceEquals(SelectedPage, Tools) ? 135
        : ReferenceEquals(SelectedPage, Instances) ? Instances.IsSelection ? 175 : 150
        : ReferenceEquals(SelectedPage, Home) ? 300 : 0, 0, 0, 0);
    public string? ErrorMessage => _errorMessage;
    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        await RunAsync("正在初始化", async ct =>
        {
            await Service.InitializeAsync(ct);
            foreach (var log in Service.GetRecentLogs()) Logs.Append(log);
        }, allowWhileRunning: true);
        if (!_disposed) { Home.StartCommunityRefresh(); Downloads.OnNavigatedTo(); }
    }

    public async Task<bool> RunAsync(string label, Func<CancellationToken, Task> action, bool allowWhileRunning = false, params string?[] sensitiveValues)
    {
        if (!(allowWhileRunning ? CanReadRemote : CanMutate)) return false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); operationCompletion = completion;
        _sensitiveValues = sensitiveValues;
        if (!backgroundInstall) handledBackgroundError = null;
        _executing = true;
        _operationLabel = label;
        ShowError(null);
        NotifyState();
        try
        {
            await action(operation.Token);
            _operationLabel = label + "已完成";
            return true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _operationLabel = "操作已取消";
            return false;
        }
        catch (Exception ex)
        {
            _operationLabel = label + "失败";
            if (backgroundFailure is { } failure) failure(ex);
            else ShowError(UiPrivacy.Redact(ex.Message, sensitiveValues));
            return false;
        }
        finally
        {
            _executing = false;
            _operation = null;
            if (!_disposed) ApplySnapshot(Service.Snapshot);
            _sensitiveValues = [];
            completion.TrySetResult();
        }
    }

    public async Task CancelAndWaitForCloseAsync()
    {
        closing = true;
        var pending = operationCompletion?.Task ?? Task.CompletedTask;
        CancelCurrentOperation();
        try
        {
            await Task.WhenAll(pending, Tools.Toolbox.CancelAndWaitAsync(), Settings.CancelAndWaitAsync(), Instances.CancelComponentWorkAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { closing = false; NotifyState(); }
    }

    public async Task RunControlAsync(string label, Func<CancellationToken, Task> action)
    {
        if (_disposed) return;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(label + "失败：" + UiPrivacy.Redact(ex.Message)); }
        finally { if (!_disposed) ApplySnapshot(Service.Snapshot); }
    }

    public void ShowError(string? message)
    {
        _errorMessage = string.IsNullOrWhiteSpace(message) ? null : UiPrivacy.Redact(message, _sensitiveValues);
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }

    internal void Dispatch(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }

    private void OnSnapshotChanged(object? sender, LauncherSnapshot snapshot) => Dispatch(() => ApplySnapshot(Service.Snapshot));
    private void OnInstancesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Instances.ContextTitle) or nameof(Instances.WorldContextTitle) or nameof(Instances.IsWorldDetails) && ReferenceEquals(SelectedPage, Instances))
            OnPropertyChanged(nameof(NavigationTitle));
    }
    private void OnDownloadsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(SelectedPage, Downloads) && e.PropertyName is nameof(Downloads.IsResourceDetail) or nameof(Downloads.ProjectTitle) or nameof(Downloads.IsTasksPage))
            NotifyNavigation();
    }
    private void OnLogReceived(object? sender, LauncherLog log)
    {
        var safeLog = log with { Message = UiPrivacy.Redact(log.Message, _sensitiveValues) };
        Dispatch(() => Logs.Append(safeLog));
    }
    private void ApplySnapshot(LauncherSnapshot snapshot)
    {
        Snapshot = snapshot;
        Localization.UiLocalization.Instance.SetLanguage(snapshot.Settings.Language, snapshot.Settings.Launcher?.RegionalFormat ?? "system");
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            if (backgroundInstall) handledBackgroundError = snapshot.Error;
            else if (snapshot.Error != handledBackgroundError) ShowError(snapshot.Error);
        }
        foreach (var page in Pages) page.Refresh(snapshot);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = snapshot.Settings.Theme switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(BrandTitle));
        OnPropertyChanged(nameof(BuildSummary));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(PlatformSummary));
        NotifyState();
    }
    internal void NotifyState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanMutate));
        OnPropertyChanged(nameof(CanReadRemote));
        OnPropertyChanged(nameof(HasRunningGame));
        OnPropertyChanged(nameof(ShowOperationStatus));
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(OperationTransferSummary));
        OnPropertyChanged(nameof(HasOperationTransferSummary));
        NotifyLaunchPresentation();
        CancelOperationCommand.NotifyCanExecuteChanged();
        StopGameCommand.NotifyCanExecuteChanged();
        foreach (var page in Pages) page.NotifyCommands();
    }
    private void CancelCurrentOperation()
    {
        var operation = _operation;
        var token = operation?.Token ?? new CancellationToken(true);
        operation?.Cancel();
        Accounts.CancelAuthenticationState(token);
        NotifyState();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Service.SnapshotChanged -= OnSnapshotChanged;
        Service.LogReceived -= OnLogReceived;
        Instances.PropertyChanged -= OnInstancesPropertyChanged;
        Downloads.PropertyChanged -= OnDownloadsPropertyChanged;
        _operation?.Cancel();
        _lifetime.Cancel();
        Accounts.CancelAuthenticationState(_lifetime.Token);
        Home.Dispose();
        Instances.Dispose();
        Downloads.Dispose();
        Tools.Dispose();
        Settings.DisposeLauncherSettings();
        _lifetime.Dispose();
        // Bootstrap owns the service, its processes, pending writes and data lease.
    }
}

public static class UiPrivacy
{
    public static string Redact(string? value, params string?[] sensitiveValues)
    {
        var result = value ?? "";
        foreach (var secret in sensitiveValues.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s!.Length))
            result = result.Replace(secret!, "[已隐藏]", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?i)(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?token|client[_-]?secret|api[_-]?key|token|secret|password|passwd|rpc[_-]?secret|authorization|code_verifier)\b[""']?\s*[:=]\s*[""']?)(?:Bearer\s+)?[^\s,""';&]+", "$1[已隐藏]");
        return Regex.Replace(result, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [已隐藏]");
    }
}
