using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool backgroundInstall;
    private Action<Exception>? backgroundFailure;
    private string? handledBackgroundError;
    private DownloadsPageViewModel? notificationDownloads;
    private bool taskNotificationVisible;
    private string taskNotificationTitle = "", taskNotificationMessage = "";
    internal bool HasBackgroundInstall => backgroundInstall;
    internal bool CanBrowseDownloads => !_disposed && !closing && (CanReadRemote || backgroundInstall && _executing);
    public bool IsTaskNotificationVisible { get => taskNotificationVisible; private set => SetProperty(ref taskNotificationVisible, value); }
    public string TaskNotificationTitle { get => taskNotificationTitle; private set => SetProperty(ref taskNotificationTitle, value); }
    public string TaskNotificationMessage { get => taskNotificationMessage; private set => SetProperty(ref taskNotificationMessage, value); }
    public RelayCommand ViewTaskNotificationCommand { get; private set; } = null!;
    public RelayCommand DismissTaskNotificationCommand { get; private set; } = null!;
    private void InitializeTaskNotifications()
    {
        ViewTaskNotificationCommand = new(() =>
        {
            if (notificationDownloads is not { } downloads || _disposed) return;
            downloads.SelectCategory("tasks"); SelectedPage = downloads;
            IsTaskNotificationVisible = false;
        });
        DismissTaskNotificationCommand = new(() => IsTaskNotificationVisible = false);
    }
    internal void ShowTaskNotification(DownloadsPageViewModel source, string title, string message)
    {
        if (_disposed || closing) return;
        notificationDownloads = source;
        TaskNotificationTitle = UiPrivacy.Redact(title);
        TaskNotificationMessage = UiPrivacy.Redact(message);
        IsTaskNotificationVisible = true;
    }
    internal async Task<bool> RunBackgroundInstallAsync(string label, Func<CancellationToken, Task> action, Action<Exception> failure)
    {
        if (!CanMutate || backgroundInstall) return false;
        backgroundInstall = true; backgroundFailure = failure;
        try { return await RunAsync(label, action); }
        finally { backgroundInstall = false; backgroundFailure = null; if (!_disposed) NotifyState(); }
    }
}
