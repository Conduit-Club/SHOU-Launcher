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
    protected ILauncherService Service => Owner.Service;
    public string Title { get; } = title;
    public string Description { get; } = description;
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

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    internal ILauncherService Service { get; }
    public IExternalLauncher ExternalLauncher { get; }
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _executing;
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
    public ObservableCollection<PageViewModel> Pages { get; }
    public RelayCommand CancelOperationCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public AsyncRelayCommand StopGameCommand { get; }

    public MainWindowViewModel(ILauncherService service, IExternalLauncher externalLauncher)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        ExternalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        Snapshot = service.Snapshot;
        Home = new(this);
        Instances = new(this);
        Downloads = new(this);
        Accounts = new(this);
        Settings = new(this);
        Logs = new(this);
        Pages = [Home, Instances, Downloads, Accounts, Settings, Logs];
        _selectedPage = Home;
        CancelOperationCommand = new(() => { _operation?.Cancel(); NotifyState(); }, () => _operation is { IsCancellationRequested: false });
        DismissErrorCommand = new(() => ShowError(null));
        StopGameCommand = new(() => RunControlAsync("停止游戏", ct => Service.StopGameAsync(ct)), () => HasRunningGame);
        Service.SnapshotChanged += OnSnapshotChanged;
        Service.LogReceived += OnLogReceived;
        ApplySnapshot(Snapshot);
    }

    public PageViewModel SelectedPage { get => _selectedPage; set { if (value is not null) SetProperty(ref _selectedPage, value); } }
    public string WindowTitle => Snapshot.Build.Name;
    public string BuildSummary => $"{Snapshot.Build.Version} · {Snapshot.Build.Codename} · {Snapshot.Build.Configuration}";
    public string SourceSummary => Snapshot.Build.SourceSha.Length > 12 ? Snapshot.Build.SourceSha[..12] : Snapshot.Build.SourceSha;
    public string PlatformSummary => $"{Snapshot.Platform.OS} · 硬件 {Snapshot.Platform.HardwareArchitecture} / 进程 {Snapshot.Platform.ProcessArchitecture}";
    public string StateSummary => _executing ? _operationLabel : Snapshot.State switch
    {
        "Installing" => "正在安装", "Launching" => "正在启动", "Running" => $"游戏运行中 · PID {Snapshot.GameProcessId}",
        "Authenticating" => "正在验证账户", "Error" => "操作失败，可修正后重试", _ => _operationLabel
    };
    public bool IsBusy => _executing || Snapshot.State is "Installing" or "Launching" or "Authenticating";
    public bool CanMutate => !_disposed && !_executing && Snapshot.State is "Ready" or "Error";
    public bool CanReadRemote => !_disposed && !_executing && Snapshot.State is "Ready" or "Error" or "Running";
    public bool HasRunningGame => !_disposed && (Snapshot.State == "Running" || Snapshot.GameProcessId is not null);
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
    }

    public async Task<bool> RunAsync(string label, Func<CancellationToken, Task> action, bool allowWhileRunning = false, params string?[] sensitiveValues)
    {
        if (!(allowWhileRunning ? CanReadRemote : CanMutate)) return false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _sensitiveValues = sensitiveValues;
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
            ShowError(UiPrivacy.Redact(ex.Message, sensitiveValues));
            return false;
        }
        finally
        {
            _executing = false;
            _operation = null;
            if (!_disposed) ApplySnapshot(Service.Snapshot);
            _sensitiveValues = [];
        }
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
    private void OnLogReceived(object? sender, LauncherLog log)
    {
        var safeLog = log with { Message = UiPrivacy.Redact(log.Message, _sensitiveValues) };
        Dispatch(() => Logs.Append(safeLog));
    }
    private void ApplySnapshot(LauncherSnapshot snapshot)
    {
        Snapshot = snapshot;
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) ShowError(snapshot.Error);
        foreach (var page in Pages) page.Refresh(snapshot);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = snapshot.Settings.Theme switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(WindowTitle));
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
        OnPropertyChanged(nameof(StateSummary));
        CancelOperationCommand.NotifyCanExecuteChanged();
        StopGameCommand.NotifyCanExecuteChanged();
        foreach (var page in Pages) page.NotifyCommands();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Service.SnapshotChanged -= OnSnapshotChanged;
        Service.LogReceived -= OnLogReceived;
        _operation?.Cancel();
        _lifetime.Cancel();
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
