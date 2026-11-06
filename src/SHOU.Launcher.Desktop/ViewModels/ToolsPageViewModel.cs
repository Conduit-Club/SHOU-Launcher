using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ToolsPageViewModel : PageViewModel
{
    public JavaTrustToolsViewModel JavaTrust { get; }
    private bool _isJavaTrust = true;
    public bool IsJavaTrust => _isJavaTrust;
    public bool IsToolbox => !_isJavaTrust;
    public string ToolboxCapabilityReason => "以下工具尚未迁移；当前可从社团入口查看指南，或打开数据目录与日志。";
    public string DataDirectory => Service.Paths.DataDirectory;
    public RelayCommand ShowJavaTrustCommand { get; }
    public RelayCommand ShowJavaSettingsCommand { get; }
    public RelayCommand ShowToolboxCommand { get; }

    public ToolsPageViewModel(MainWindowViewModel owner) : base(owner, "工具", "Java 证书诊断与百宝箱")
    {
        JavaTrust = new(owner);
        ShowJavaTrustCommand = Command(() => SelectPage(true));
        ShowJavaSettingsCommand = Command(() => { Owner.Settings.SelectedSection = "java"; Owner.ShowSettingsCommand.Execute(null); });
        ShowToolboxCommand = Command(() => SelectPage(false));
    }
    public override void Refresh(SHOU.Launcher.Core.Abstractions.LauncherSnapshot snapshot) { JavaTrust.Refresh(snapshot); base.Refresh(snapshot); }
    public override void NotifyCommands() { JavaTrust?.NotifyCommands(); base.NotifyCommands(); }

    private void SelectPage(bool isJavaTrust)
    {
        if (!SetProperty(ref _isJavaTrust, isJavaTrust, nameof(IsJavaTrust))) OnPropertyChanged(nameof(IsJavaTrust));
        OnPropertyChanged(nameof(IsToolbox));
    }
}
