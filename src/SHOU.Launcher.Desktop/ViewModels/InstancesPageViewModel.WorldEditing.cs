using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record WorldPermissionChoice(bool Value, string Name);
public sealed record WorldDifficultyChoice(int Value, string Name);
public sealed partial class InstancesPageViewModel
{
    private sealed class WorldSettingsDraft
    {
        public WorldEditState? State;
        public WorldReadPhase Phase;
        public bool AllowCommands, DifficultyLocked, Dirty, Conflict;
        public int Difficulty = 2;
        public string Feedback = "", Backup = "";
    }
    private readonly Dictionary<string, WorldSettingsDraft> worldSettingsDrafts = new(StringComparer.Ordinal);
    private readonly WorldSettingsDraft emptyWorldSettings = new();
    private bool worldEditingReady, worldEditingReadScheduled;
    private string? WorldSettingsKey => SelectedInstance is { } instance && SelectedWorldRow is { } world ? instance.Id + "\0" + instance.GameDirectory + "\0" + world.DirectoryName : null;
    private WorldSettingsDraft CurrentWorldSettings
    {
        get { if (WorldSettingsKey is not { } key) return emptyWorldSettings; if (!worldSettingsDrafts.TryGetValue(key, out var draft)) worldSettingsDrafts[key] = draft = new(); return draft; }
    }
    public IReadOnlyList<WorldPermissionChoice> WorldPermissionChoices { get; } = [new(false, "不允许"), new(true, "允许")];
    public IReadOnlyList<WorldDifficultyChoice> WorldDifficultyChoices { get; } = [new(0, "和平"), new(1, "简单"), new(2, "普通"), new(3, "困难")];
    public WorldPermissionChoice SelectedWorldPermission
    {
        get => WorldPermissionChoices.First(c => c.Value == CurrentWorldSettings.AllowCommands);
        set { if (value is null || !CanEditWorldMetadata || value.Value == CurrentWorldSettings.AllowCommands) return; CurrentWorldSettings.AllowCommands = value.Value; MarkWorldSettingsChanged(); }
    }
    public WorldDifficultyChoice SelectedWorldDifficulty
    {
        get => WorldDifficultyChoices.First(c => c.Value == CurrentWorldSettings.Difficulty);
        set { if (value is null || !CanEditWorldDifficulty || value.Value is < 0 or > 3 || value.Value == CurrentWorldSettings.Difficulty) return; CurrentWorldSettings.Difficulty = value.Value; MarkWorldSettingsChanged(); }
    }
    public bool WorldDifficultyLocked
    {
        get => CurrentWorldSettings.DifficultyLocked;
        set { if (!CanEditWorldMetadata || CurrentWorldSettings.DifficultyLocked == value) return; CurrentWorldSettings.DifficultyLocked = value; MarkWorldSettingsChanged(); }
    }
    public bool IsWorldSettingsDirty => CurrentWorldSettings.Dirty;
    public bool HasWorldSettingsConflict => CurrentWorldSettings.Conflict;
    public bool ShowWorldSettingsSaveActions => IsWorldSettingsDirty || HasWorldSettingsConflict;
    public bool CanEditWorldMetadata => worldEditingReady && WorldWorkspaceCanWrite && CurrentWorldSettings is { Phase: WorldReadPhase.Ready, Conflict: false, State.CanEditMetadata: true };
    public bool CanEditWorldDifficulty => CanEditWorldMetadata && !WorldDifficultyLocked && SelectedWorldRow?.Summary.Hardcore != true;
    public string WorldSettingsStatus => Service is not IInstanceWorldEditService ? "当前服务未提供世界设置与启停写入。"
        : CurrentWorldSettings.Conflict ? "文件已有变化或保存未完成；草稿保留，请核对后显式放弃并重新读取。"
        : CurrentWorldSettings.Phase switch { WorldReadPhase.Unread => "尚未读取世界设置。", WorldReadPhase.Loading => "正在读取世界设置与数据包状态…", WorldReadPhase.Error => "读取未完成，原文件与草稿保留。", _ => CurrentWorldSettings.State?.Capability ?? "尚未读取世界设置。" };
    public string WorldSettingsFeedback => CurrentWorldSettings.Feedback;
    public string WorldEditBackupPath => UiPrivacy.Redact(CurrentWorldSettings.Backup);
    public bool HasWorldEditBackup => CurrentWorldSettings.Backup.Length > 0;
    public string WorldSpawnText => CurrentWorldSettings.State?.Values is { SpawnX: { } x, SpawnY: { } y, SpawnZ: { } z } ? $"{x} / {y} / {z}" : "未知";
    public string WorldRuntimeText => CurrentWorldSettings.State?.Values.WorldTicks is >= 0 and var ticks ? $"{ticks / 72000:N0} 小时 {(ticks / 1200) % 60:N0} 分 {(ticks / 20) % 60:N0} 秒（世界运行时间）" : "未知";
    public bool CanEnableSelectedDatapacks => CanChangeSelectedPacks && CurrentWorldPacks.Selected.Any(row => row.Details?.Enabled != true);
    public bool CanDisableSelectedDatapacks => CanChangeSelectedPacks && CurrentWorldPacks.Selected.Any(row => row.Details?.Enabled != false);
    private bool CanChangeSelectedPacks => worldEditingReady && IsWorldDatapacksPage && WorldWorkspaceCanWrite && CurrentWorldSettings is { Phase: WorldReadPhase.Ready, Conflict: false, State.CanToggleDatapacks: true }
        && CurrentWorldPacks.Selected.Count > 0 && CurrentWorldPacks.Selected.All(row => row.Details is { CanToggle: true, ContentRevision.Length: > 0 });
    public AsyncRelayCommand RefreshWorldEditCommand { get; private set; } = null!;
    public AsyncRelayCommand DiscardWorldSettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveWorldSettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand EnableWorldDatapacksCommand { get; private set; } = null!;
    public AsyncRelayCommand DisableWorldDatapacksCommand { get; private set; } = null!;
    private void InitializeWorldEditing()
    {
        if (worldEditingReady) return;
        RefreshWorldEditCommand = AsyncCommand(() => ReadWorldEditStateAsync(false), () => WorldCanReadEdits && !IsWorldSettingsDirty);
        DiscardWorldSettingsCommand = AsyncCommand(() => ReadWorldEditStateAsync(true), () => WorldCanReadEdits && (IsWorldSettingsDirty || HasWorldSettingsConflict));
        SaveWorldSettingsCommand = AsyncCommand(SaveWorldSettingsAsync, () => CanEditWorldMetadata && IsWorldSettingsDirty);
        EnableWorldDatapacksCommand = AsyncCommand(() => ChangeSelectedDatapacksAsync(true), () => CanEnableSelectedDatapacks);
        DisableWorldDatapacksCommand = AsyncCommand(() => ChangeSelectedDatapacksAsync(false), () => CanDisableSelectedDatapacks);
        foreach (var command in new[] { RefreshWorldEditCommand, DiscardWorldSettingsCommand, SaveWorldSettingsCommand, EnableWorldDatapacksCommand, DisableWorldDatapacksCommand })
            command.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AsyncRelayCommand.IsRunning) && !command.IsRunning) ScheduleUnreadWorldEditing(); };
        worldEditingReady = true;
    }
    private bool WorldCanReadEdits => worldEditingReady && IsWorldDetails && WorldWorkspaceCanRead && Service is IInstanceWorldEditService;
    private void MarkWorldSettingsChanged()
    {
        var draft = CurrentWorldSettings;
        draft.Dirty = draft.State is { } state && (state.Values.AllowCommands != draft.AllowCommands || state.Values.Difficulty != draft.Difficulty || state.Values.DifficultyLocked != draft.DifficultyLocked);
        draft.Feedback = draft.Dirty ? "有未保存的世界设置，仅点击保存后写入 level.dat。" : ""; NotifyWorldEditing();
    }
    private void NotifyWorldEditing()
    {
        if (!worldEditingReady) return;
        ApplyWorldPackDetails();
        foreach (var name in new[] { nameof(SelectedWorldPermission), nameof(SelectedWorldDifficulty), nameof(WorldDifficultyLocked), nameof(IsWorldSettingsDirty), nameof(HasWorldSettingsConflict), nameof(ShowWorldSettingsSaveActions), nameof(CanEditWorldMetadata), nameof(CanEditWorldDifficulty), nameof(WorldSettingsStatus), nameof(WorldSettingsFeedback), nameof(WorldEditBackupPath), nameof(HasWorldEditBackup), nameof(WorldSpawnText), nameof(WorldRuntimeText), nameof(CanEnableSelectedDatapacks), nameof(CanDisableSelectedDatapacks) }) OnPropertyChanged(name);
        NotifyWorldEditingCommands(); ScheduleUnreadWorldEditing();
    }
    private void NotifyWorldEditingCommands()
    {
        if (!worldEditingReady) return;
        foreach (var command in new IRelayCommand[] { RefreshWorldEditCommand, DiscardWorldSettingsCommand, SaveWorldSettingsCommand, EnableWorldDatapacksCommand, DisableWorldDatapacksCommand }) command.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEnableSelectedDatapacks)); OnPropertyChanged(nameof(CanDisableSelectedDatapacks));
    }
    private void ScheduleUnreadWorldEditing()
    {
        if (!worldEditingReady || Service is not IInstanceWorldEditService || worldEditingReadScheduled || !IsWorldDetails || CurrentWorldSettings.Phase != WorldReadPhase.Unread || !ReferenceEquals(Owner.SelectedPage, this)) return;
        worldEditingReadScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            worldEditingReadScheduled = false;
            if (WorldCanReadEdits && CurrentWorldSettings.Phase == WorldReadPhase.Unread && ReferenceEquals(Owner.SelectedPage, this) && RefreshWorldEditCommand is { IsRunning: false } && RefreshWorldEditCommand.CanExecute(null)) RefreshWorldEditCommand.Execute(null);
        }, DispatcherPriority.Background);
    }
    private async Task ReadWorldEditStateAsync(bool discard)
    {
        if (!WorldCanReadEdits || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || CurrentWorldSettings.Phase == WorldReadPhase.Loading || !discard && IsWorldSettingsDirty) return;
        var key = WorldSettingsKey; var draft = CurrentWorldSettings; var previousPhase = draft.Phase;
        draft.Phase = WorldReadPhase.Loading; NotifyWorldEditing(); WorldEditState? result = null; var cancelled = false;
        var completed = await Owner.RunAsync("读取存档设置", async ct =>
        {
            try { result = await ((IInstanceWorldEditService)Service).GetWorldEditStateAsync(instance.Id, world.DirectoryName, ct); ct.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; throw; }
        }, true);
        if (completed && result is not null)
        {
            draft.State = result; draft.AllowCommands = result.Values.AllowCommands ?? false; draft.Difficulty = result.Values.Difficulty ?? 2; draft.DifficultyLocked = result.Values.DifficultyLocked ?? false;
            draft.Dirty = false; draft.Conflict = false; draft.Phase = WorldReadPhase.Ready; draft.Feedback = "";
        }
        else if (cancelled) { draft.Phase = draft.State is not null ? previousPhase : WorldReadPhase.Error; draft.Feedback = "读取已取消，已有草稿与上次读取状态保留。"; }
        else { draft.Phase = WorldReadPhase.Error; draft.Feedback = "读取未完成，已有草稿保留。"; }
        if (key == WorldSettingsKey) NotifyWorldEditing();
    }
    private void ApplyWorldPackDetails()
    {
        if (CurrentWorldSettings.State is not { } state) return;
        var map = state.Datapacks.ToDictionary(pack => pack.File.Name, StringComparer.Ordinal);
        var changed = false;
        foreach (var row in CurrentWorldPacks.Rows) { var details = map.GetValueOrDefault(row.Name); changed |= row.Details != details; row.Details = details; }
        if (changed && CurrentWorldPacks.Query.Trim().Length > 0) { FilterPackRows(CurrentWorldPacks); OnPropertyChanged(nameof(DatapackCountSummary)); }
    }
    private async Task SaveWorldSettingsAsync()
    {
        if (!CanEditWorldMetadata || !IsWorldSettingsDirty || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || CurrentWorldSettings.State is not { } state) return;
        var key = WorldSettingsKey; var draft = CurrentWorldSettings; var edit = new WorldMetadataEdit(draft.AllowCommands, draft.Difficulty, draft.DifficultyLocked); WorldEditReceipt? receipt = null;
        var completed = await Owner.RunAsync("保存世界设置", async ct => receipt = await ((IInstanceWorldEditService)Service).SaveWorldMetadataAsync(instance.Id, world.DirectoryName, state.Revision, edit, ct));
        if (completed && receipt is not null)
        {
            draft.State = state with { Revision = receipt.Revision, Values = state.Values with { AllowCommands = edit.AllowCommands, Difficulty = edit.Difficulty, DifficultyLocked = edit.DifficultyLocked } };
            draft.Dirty = false; draft.Conflict = false; draft.Backup = receipt.BackupPath ?? draft.Backup; draft.Feedback = "世界设置已保存；原 level.dat 已备份。";
        }
        else { draft.Conflict = true; draft.Feedback = "设置保存未完成，草稿保留；请重新读取核对。"; }
        if (key == WorldSettingsKey) NotifyWorldEditing();
    }
    private async Task ChangeSelectedDatapacksAsync(bool enabled)
    {
        if (!(enabled ? CanEnableSelectedDatapacks : CanDisableSelectedDatapacks) || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || CurrentWorldSettings.State is not { } state) return;
        var key = WorldSettingsKey; var draft = CurrentWorldSettings; var selections = CurrentWorldPacks.Selected.Select(row => new WorldDatapackSelection(row.Details!.PackId, row.Details.ContentRevision)).ToArray();
        WorldEditReceipt? receipt = null;
        var completed = await Owner.RunAsync(enabled ? "启用世界数据包" : "禁用世界数据包", async ct => receipt = await ((IInstanceWorldEditService)Service).SetWorldDatapacksEnabledAsync(instance.Id, world.DirectoryName, state.Revision, selections, enabled, ct));
        if (completed && receipt is not null)
        {
            var ids = selections.Select(p => p.PackId).ToHashSet(StringComparer.Ordinal);
            draft.State = state with { Revision = receipt.Revision, Datapacks = state.Datapacks.Select(pack => ids.Contains(pack.PackId) ? pack with { Enabled = enabled } : pack).ToArray() };
            draft.Backup = receipt.BackupPath ?? draft.Backup; draft.Feedback = enabled ? "数据包启用状态已保存，下次进入世界时生效。" : "数据包禁用状态已保存，下次进入世界时生效。";
        }
        else { draft.Conflict = true; draft.Feedback = "启停保存未完成，选择与设置草稿保留；请重新读取核对。"; }
        if (key == WorldSettingsKey) NotifyWorldEditing();
    }
}
