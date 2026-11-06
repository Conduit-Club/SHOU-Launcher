using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private string _savedJavaExecutable = "";
    private bool _javaSelectionConflict;
    public bool HasJavaSelectionConflict
    {
        get => _javaSelectionConflict;
        private set { if (SetProperty(ref _javaSelectionConflict, value)) { OnPropertyChanged(nameof(SaveSummary)); NotifyCommands(); } }
    }
    public string SavedJavaSelection => Owner.Snapshot.Settings.JavaExecutable ?? "自动选择 Java";
    public RelayCommand UseSavedJavaSelectionCommand { get; private set; } = null!;
    public RelayCommand KeepJavaDraftCommand { get; private set; } = null!;
    private void InitializeJavaSelectionConflict()
    {
        UseSavedJavaSelectionCommand = Command(() =>
        {
            _savedJavaExecutable = Owner.Snapshot.Settings.JavaExecutable ?? "";
            JavaExecutable = _savedJavaExecutable; HasJavaSelectionConflict = false;
        }, () => CanEdit && HasJavaSelectionConflict);
        KeepJavaDraftCommand = Command(() =>
        {
            _savedJavaExecutable = Owner.Snapshot.Settings.JavaExecutable ?? ""; HasJavaSelectionConflict = false;
        }, () => CanEdit && HasJavaSelectionConflict);
    }
    private static bool EqualJavaSelection(string first, string second) => first.Trim().Equals(second.Trim(),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private void ReconcileJavaSelection(LauncherSettings latest)
    {
        var saved = latest.JavaExecutable ?? "";
        if (EqualJavaSelection(JavaExecutable, _savedJavaExecutable))
        {
            _loading = true;
            try { JavaExecutable = saved; _savedJavaExecutable = saved; HasJavaSelectionConflict = false; }
            finally { _loading = false; }
        }
        else if (EqualJavaSelection(JavaExecutable, saved)) { _savedJavaExecutable = saved; HasJavaSelectionConflict = false; }
        else HasJavaSelectionConflict = !EqualJavaSelection(saved, _savedJavaExecutable);
        OnPropertyChanged(nameof(SavedJavaSelection));
    }
}
