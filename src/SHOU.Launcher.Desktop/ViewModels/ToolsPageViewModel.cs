using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ToolsPageViewModel : PageViewModel
{
    private bool _isLobby = true;
    public bool IsLobby => _isLobby;
    public bool IsToolbox => !_isLobby;
    public string LobbyCapabilityReason => "联机服务尚未迁移，当前无法创建或加入大厅。";
    public string ToolboxCapabilityReason => "以下工具尚未迁移；当前可从社团入口查看指南，或打开数据目录与日志。";
    public string DataDirectory => Service.Paths.DataDirectory;
    public RelayCommand ShowLobbyCommand { get; }
    public RelayCommand ShowToolboxCommand { get; }

    public ToolsPageViewModel(MainWindowViewModel owner) : base(owner, "工具", "联机大厅与百宝箱")
    {
        ShowLobbyCommand = Command(() => SelectPage(true));
        ShowToolboxCommand = Command(() => SelectPage(false));
    }

    private void SelectPage(bool isLobby)
    {
        if (!SetProperty(ref _isLobby, isLobby, nameof(IsLobby))) OnPropertyChanged(nameof(IsLobby));
        OnPropertyChanged(nameof(IsToolbox));
    }
}
