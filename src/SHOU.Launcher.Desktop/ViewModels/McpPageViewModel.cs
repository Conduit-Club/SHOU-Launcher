using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Desktop.Mcp;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class McpPageViewModel : ObservableObject
{
    private readonly LauncherMcpServer server;
    private int port;
    private bool busy;
    private string feedback = "";

    internal McpPageViewModel(LauncherMcpServer server)
    {
        this.server = server;
        port = server.ReadPort();
        StartCommand = new(() => ChangeStateAsync(true), () => CanEditPort);
        PauseCommand = new(() => ChangeStateAsync(false), () => IsRunning && !busy);
    }

    public int Port
    {
        get => port;
        set
        {
            if (CanEditPort && SetProperty(ref port, value)) OnPropertyChanged(nameof(Endpoint));
        }
    }
    public string Endpoint => LauncherMcpServer.Endpoint(port);
    public bool IsRunning => server.IsRunning;
    public bool CanCopyToken => IsRunning && !busy;
    public bool CanEditPort => !IsRunning && !busy;
    public string Status => busy ? "正在切换服务状态…" : IsRunning ? "运行中" : "已暂停";
    public string Feedback
    {
        get => feedback;
        private set { if (SetProperty(ref feedback, value)) OnPropertyChanged(nameof(HasFeedback)); }
    }
    public bool HasFeedback => Feedback.Length > 0;
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    internal string Token => server.Token;
    internal void ReportAddressCopied() => Feedback = "连接地址已复制。";
    internal void ReportTokenCopied() => Feedback = "令牌已复制。";
    internal void ReportSkillExported() => Feedback = "Agent 技能已导出。";

    private async Task ChangeStateAsync(bool start)
    {
        busy = true;
        Feedback = "";
        NotifyState();
        try
        {
            if (start) await server.StartAsync(port);
            else await server.PauseAsync();
        }
        catch (Exception ex)
        {
            var label = Localization.UiLocalization.Instance.Text("MCP 服务启动或暂停失败");
            Feedback = $"{label} ({UiPrivacy.Redact(ex.Message)})";
        }
        finally
        {
            busy = false;
            NotifyState();
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanCopyToken));
        OnPropertyChanged(nameof(CanEditPort));
        OnPropertyChanged(nameof(Status));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }
}
