using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private sealed record PackRemovalDraft(string Key, string Revision, IReadOnlyList<WorldDatapackSelection> Packs, string Names);
    private PackRemovalDraft? _packRemoval;
    private string? _trashWorldKey;
    private WorldDatapackTrashEntry? _selectedTrash;
    public ObservableCollection<WorldDatapackTrashEntry> WorldDatapackTrash { get; } = [];
    public WorldDatapackTrashEntry? SelectedWorldDatapackTrash { get => _selectedTrash; set { if (SetProperty(ref _selectedTrash, value)) NotifyWorldActions(); } }
    public string SelectedWorldTrashStatus => SelectedWorldDatapackTrash?.Status ?? "";
    public bool HasWorldPackRemovalConfirmation => _packRemoval is { } draft && draft.Key == WorldSettingsKey;
    public string WorldPackRemovalNames => HasWorldPackRemovalConfirmation ? _packRemoval!.Names : "";
    public RelayCommand DownloadWorldResourcesCommand { get; private set; } = null!;
    public RelayCommand DownloadWorldDatapacksCommand { get; private set; } = null!;
    public RelayCommand RequestRecycleWorldDatapacksCommand { get; private set; } = null!;
    public RelayCommand CancelRecycleWorldDatapacksCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmRecycleWorldDatapacksCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshWorldDatapackTrashCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreWorldDatapacksCommand { get; private set; } = null!;
    private bool CanRecyclePacks => Service is IWorldDatapackTrashService && CanChangeSelectedPacks && !IsWorldSettingsDirty && CurrentWorldPacks.Selected.Count <= 16;
    private void InitializeWorldActions()
    {
        DownloadWorldResourcesCommand = Command(() => OpenWorldDownloadBrowser(false), () => WorldWorkspaceCanRead && Service is IWorldContentInstallationService);
        DownloadWorldDatapacksCommand = Command(() => OpenWorldDownloadBrowser(true), () => CanImportWorldDatapack && Service is IWorldContentInstallationService);
        RequestRecycleWorldDatapacksCommand = Command(() =>
        {
            _packRemoval = new(WorldSettingsKey!, CurrentWorldSettings.State!.Revision,
                CurrentWorldPacks.Selected.Select(p => new WorldDatapackSelection(p.Details!.PackId, p.Details.ContentRevision)).ToArray(),
                string.Join("、", CurrentWorldPacks.Selected.Select(p => p.Name)));
            NotifyWorldActions();
        }, () => CanRecyclePacks);
        CancelRecycleWorldDatapacksCommand = Command(() => { _packRemoval = null; NotifyWorldActions(); });
        ConfirmRecycleWorldDatapacksCommand = AsyncCommand(RecycleSelectedWorldDatapacksAsync, () => WorldWorkspaceCanWrite && IsWorldDatapacksPage && !IsWorldSettingsDirty && HasWorldPackRemovalConfirmation);
        RefreshWorldDatapackTrashCommand = AsyncCommand(ReadWorldDatapackTrashAsync, () => WorldWorkspaceCanRead && IsWorldDatapacksPage && Service is IWorldDatapackTrashService);
        RestoreWorldDatapacksCommand = AsyncCommand(RestoreSelectedWorldDatapacksAsync, () => WorldWorkspaceCanWrite && IsWorldDatapacksPage && !IsWorldSettingsDirty && SelectedWorldDatapackTrash is { CanRestore: true } && _trashWorldKey == WorldSettingsKey);
    }
    private void OpenWorldDownloadBrowser(bool datapack)
    {
        if (SelectedInstance is not { } instance) return;
        try
        {
            Owner.Downloads.OpenWorldContentBrowser(instance, datapack ? SelectedWorldRow?.DirectoryName : null);
            Owner.SelectedPage = Owner.Downloads;
        }
        catch (InvalidOperationException ex) { Owner.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    public async Task<bool> ImportClipboardWorldDatapackAsync(IReadOnlyList<string> localFiles, string instanceId, string worldDirectory, string gameDirectory)
    {
        if (localFiles.Count != 1 || !Path.IsPathFullyQualified(localFiles[0]) || !Path.GetExtension(localFiles[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        { Owner.ShowError("请在文件管理器中复制一个本地 ZIP 数据包文件后再粘贴。"); return false; }
        return await ImportWorldDatapackFileAsync(localFiles[0], instanceId, worldDirectory, gameDirectory);
    }
    private async Task ReadWorldDatapackTrashAsync()
    {
        if (Service is not IWorldDatapackTrashService service || SelectedInstance is not { } instance || SelectedWorldRow is not { } world) return;
        var key = WorldSettingsKey; IReadOnlyList<WorldDatapackTrashEntry>? entries = null;
        var success = await Owner.RunInlineAsync("读取数据包回收记录", async ct => entries = await service.GetWorldDatapackTrashAsync(instance.Id, world.DirectoryName, ct));
        if (key != WorldSettingsKey) return;
        WorldDatapackTrash.Clear(); _selectedTrash = null; _trashWorldKey = success ? key : null;
        if (entries is not null) foreach (var entry in entries) WorldDatapackTrash.Add(entry);
        OnPropertyChanged(nameof(SelectedWorldDatapackTrash)); NotifyWorldActions();
    }
    private async Task RecycleSelectedWorldDatapacksAsync()
    {
        if (!HasWorldPackRemovalConfirmation || _packRemoval is not { } removal || Service is not IWorldDatapackTrashService service || SelectedInstance is not { } instance || SelectedWorldRow is not { } world) return;
        _packRemoval = null; var draft = _worldDraft;
        var success = await Owner.RunInlineAsync("回收数据包", ct => service.RecycleWorldDatapacksAsync(instance.Id, world.DirectoryName, removal.Revision, removal.Packs, ct));
        draft.Feedback = success ? "数据包已移入回收记录，可在当前存档页恢复。" : Owner.ErrorMessage ?? "回收已取消，原文件保留。";
        if (WorldSettingsKey == removal.Key) await RefreshAfterPackTrashAsync();
    }
    private async Task RestoreSelectedWorldDatapacksAsync()
    {
        if (SelectedWorldDatapackTrash is not { CanRestore: true } entry || _trashWorldKey != WorldSettingsKey || Service is not IWorldDatapackTrashService service || SelectedInstance is not { } instance || SelectedWorldRow is not { } world) return;
        var key = WorldSettingsKey; var draft = _worldDraft;
        var success = await Owner.RunInlineAsync("恢复数据包", ct => service.RestoreWorldDatapacksAsync(instance.Id, world.DirectoryName, entry.Id, ct));
        draft.Feedback = success ? "已恢复数据包、原启停状态和安装记录。" : Owner.ErrorMessage ?? "恢复已取消，回收文件保留。";
        if (key == WorldSettingsKey) await RefreshAfterPackTrashAsync();
    }
    private async Task RefreshAfterPackTrashAsync()
    {
        CurrentWorldSettings.Phase = WorldReadPhase.Unread;
        await RefreshWorldPacksAsync(); await ReadWorldDatapackTrashAsync(); NotifyWorldPresentation();
    }
    private void NotifyWorldActions()
    {
        if (DownloadWorldResourcesCommand is null) return;
        if (_packRemoval is { } draft && draft.Key != WorldSettingsKey) _packRemoval = null;
        if (_trashWorldKey != WorldSettingsKey) { WorldDatapackTrash.Clear(); _selectedTrash = null; OnPropertyChanged(nameof(SelectedWorldDatapackTrash)); }
        foreach (var property in new[] { nameof(HasWorldPackRemovalConfirmation), nameof(WorldPackRemovalNames), nameof(SelectedWorldTrashStatus) }) OnPropertyChanged(property);
        foreach (var command in new IRelayCommand[] { DownloadWorldResourcesCommand, DownloadWorldDatapacksCommand, RequestRecycleWorldDatapacksCommand, CancelRecycleWorldDatapacksCommand, ConfirmRecycleWorldDatapacksCommand, RefreshWorldDatapackTrashCommand, RestoreWorldDatapacksCommand }) command.NotifyCanExecuteChanged();
    }
}
