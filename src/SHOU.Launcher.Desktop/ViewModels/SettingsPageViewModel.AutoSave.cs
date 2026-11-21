using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private readonly DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private bool autoSaveReady, saveInProgress, saveIsAutomatic, waitingToSave;
    private long settingsEditRevision;
    private string autoSaveFeedback = "";
    public string AutoSaveFeedback { get => autoSaveFeedback; private set { if (SetProperty(ref autoSaveFeedback, value)) OnPropertyChanged(nameof(HasAutoSaveFeedback)); } }
    public bool HasAutoSaveFeedback => !string.IsNullOrWhiteSpace(AutoSaveFeedback);
    private void InitializeAutoSave()
    {
        autoSaveTimer.Tick += async (_, _) =>
        {
            autoSaveTimer.Stop();
            if (saveInProgress || settingsLifetime.IsCancellationRequested) return;
            await SaveSettingsCoreAsync(true);
        };
    }
    private void QueueAutoSave()
    {
        if (!autoSaveReady || _loading || settingsLifetime.IsCancellationRequested) return;
        autoSaveTimer.Stop(); autoSaveTimer.Start();
    }
    public async Task<bool> FlushAutoSaveAsync()
    {
        autoSaveTimer.Stop();
        if (!autoSaveReady || !IsDirty) return true;
        await SaveSettingsCoreAsync(true);
        return !IsDirty;
    }
    private void ReportSettingsError(string message)
    {
        AutoSaveFeedback = message;
        if (!saveIsAutomatic) Owner.ShowError(message);
    }
    private async Task SaveSettingsCoreAsync(bool automatic)
    {
        try { await settingsSaveGate.WaitAsync(settingsLifetime.Token); }
        catch (OperationCanceledException) { return; }
        var revision = settingsEditRevision;
        try
        {
            if (!IsDirty || settingsLifetime.IsCancellationRequested) return;
            if (!CanEdit) { waitingToSave = true; return; }
            saveInProgress = true; saveIsAutomatic = automatic; waitingToSave = false; AutoSaveFeedback = "";
            await SaveValidatedSettingsAsync(revision);
        }
        catch (OperationCanceledException) { if (!settingsLifetime.IsCancellationRequested) AutoSaveFeedback = "自动保存超时；当前输入未全部保存，请重试。"; }
        catch (Exception ex)
        {
            AutoSaveFeedback = "设置未全部保存，请检查输入或安全存储后重试。";
            if (!automatic) Owner.ShowError(UiPrivacy.Redact(ex.Message, ProxySecret, Aria2Secret));
        }
        finally
        {
            saveInProgress = false; saveIsAutomatic = false; settingsSaveGate.Release();
            if (IsDirty && revision != settingsEditRevision) QueueAutoSave();
        }
    }
}
