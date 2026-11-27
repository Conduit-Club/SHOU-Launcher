using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Desktop.Mcp;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ToolsPageViewModel : PageViewModel, IDisposable
{
    public JavaTrustToolsViewModel JavaTrust { get; }
    public ToolboxViewModel Toolbox { get; }
    public CrashAnalysisViewModel Crash { get; }
    public McpPageViewModel? Mcp { get; }
    private enum Section { JavaTrust, Toolbox, Mcp }
    private Section selected;
    public bool IsJavaTrust => selected == Section.JavaTrust;
    public bool IsToolbox => selected == Section.Toolbox;
    public bool IsMcp => selected == Section.Mcp;
    public bool HasMcp => Mcp is not null;
    public RelayCommand ShowJavaTrustCommand { get; }
    public RelayCommand ShowJavaSettingsCommand { get; }
    public RelayCommand ShowToolboxCommand { get; }
    public RelayCommand ShowMcpCommand { get; }

    internal ToolsPageViewModel(MainWindowViewModel owner, LauncherMcpServer? mcp) : base(owner, "工具", "Java 证书诊断与百宝箱")
    {
        JavaTrust = new(owner);
        Toolbox = new(owner);
        Crash = new(owner);
        Mcp = mcp is null ? null : new(mcp);
        ShowJavaTrustCommand = Command(() => SelectPage(Section.JavaTrust));
        ShowJavaSettingsCommand = Command(() => { Owner.Settings.SelectedSection = "java"; Owner.ShowSettingsCommand.Execute(null); });
        ShowToolboxCommand = Command(() => SelectPage(Section.Toolbox));
        ShowMcpCommand = Command(() => SelectPage(Section.Mcp), () => HasMcp);
    }
    public override void Refresh(SHOU.Launcher.Core.Abstractions.LauncherSnapshot snapshot) { JavaTrust.Refresh(snapshot); Toolbox.Refresh(snapshot); Crash.Refresh(snapshot); base.Refresh(snapshot); }
    public override void NotifyCommands() { JavaTrust?.NotifyCommands(); Toolbox?.NotifyCommands(); base.NotifyCommands(); }
    public void OnNavigatingAway() => Toolbox.Cancel();
    public void Dispose() { Toolbox.Dispose(); Crash.Dispose(); }

    private void SelectPage(Section section)
    {
        if (section != Section.Toolbox) Toolbox.Cancel();
        selected = section;
        OnPropertyChanged(nameof(IsJavaTrust));
        OnPropertyChanged(nameof(IsToolbox));
        OnPropertyChanged(nameof(IsMcp));
    }
}
