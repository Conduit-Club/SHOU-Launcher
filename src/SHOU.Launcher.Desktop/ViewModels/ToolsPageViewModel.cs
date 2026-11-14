using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ToolsPageViewModel : PageViewModel, IDisposable
{
    public JavaTrustToolsViewModel JavaTrust { get; }
    public ToolboxViewModel Toolbox { get; }
    public CrashAnalysisViewModel Crash { get; }
    private bool _isJavaTrust = true;
    public bool IsJavaTrust => _isJavaTrust;
    public bool IsToolbox => !_isJavaTrust;
    public RelayCommand ShowJavaTrustCommand { get; }
    public RelayCommand ShowJavaSettingsCommand { get; }
    public RelayCommand ShowToolboxCommand { get; }

    public ToolsPageViewModel(MainWindowViewModel owner) : base(owner, "工具", "Java 证书诊断与百宝箱")
    {
        JavaTrust = new(owner);
        Toolbox = new(owner);
        Crash = new(owner);
        ShowJavaTrustCommand = Command(() => SelectPage(true));
        ShowJavaSettingsCommand = Command(() => { Owner.Settings.SelectedSection = "java"; Owner.ShowSettingsCommand.Execute(null); });
        ShowToolboxCommand = Command(() => SelectPage(false));
    }
    public override void Refresh(SHOU.Launcher.Core.Abstractions.LauncherSnapshot snapshot) { JavaTrust.Refresh(snapshot); Toolbox.Refresh(snapshot); Crash.Refresh(snapshot); base.Refresh(snapshot); }
    public override void NotifyCommands() { JavaTrust?.NotifyCommands(); Toolbox?.NotifyCommands(); base.NotifyCommands(); }
    public void OnNavigatingAway() => Toolbox.Cancel();
    public void Dispose() { Toolbox.Dispose(); Crash.Dispose(); }

    private void SelectPage(bool isJavaTrust)
    {
        if (isJavaTrust) Toolbox.Cancel();
        if (!SetProperty(ref _isJavaTrust, isJavaTrust, nameof(IsJavaTrust))) OnPropertyChanged(nameof(IsJavaTrust));
        OnPropertyChanged(nameof(IsToolbox));
    }
}
