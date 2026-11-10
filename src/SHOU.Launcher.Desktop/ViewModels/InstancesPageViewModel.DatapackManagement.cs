using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private string? _packManagementKey, _updatePackId;
    private WorldDatapackOrderState? _packOrder;
    private WorldDatapackOrderItem? _selectedPackOrder;
    private WorldDatapackUpdateOptions? _packUpdateOptions;
    private WorldDatapackUpdateChoice? _packUpdateChoice;
    private WorldDatapackUpdatePreview? _packUpdatePreview;
    private bool _packOrderOpen, _packUpdateOpen, _packManagementBusy;
    private int _packManagementRevision;
    private string _packManagementFeedback = "";
    private string? PackManagementKey => SelectedInstance?.Id == Owner.Snapshot.SelectedInstanceId ? WorldSettingsKey : null;
    public ObservableCollection<WorldDatapackOrderItem> DatapackOrderItems { get; } = [];
    public IReadOnlyList<WorldDatapackUpdateChoice> DatapackUpdateChoices => _packUpdateOptions?.Choices ?? [];
    public WorldDatapackOrderItem? SelectedDatapackOrderItem { get => _selectedPackOrder; set { if (SetProperty(ref _selectedPackOrder, value)) NotifyDatapackManagement(); } }
    public WorldDatapackUpdateChoice? SelectedDatapackUpdateChoice { get => _packUpdateChoice; set { if (SetProperty(ref _packUpdateChoice, value)) { InvalidateDatapackUpdate(); NotifyDatapackManagement(); } } }
    public bool IsDatapackOrderOpen => _packOrderOpen;
    public bool IsDatapackUpdateOpen => _packUpdateOpen;
    public bool IsDatapackManagementBusy => _packManagementBusy;
    public bool ShowDatapackManagementStatus => _packOrderOpen || _packUpdateOpen || _packBackupsOpen || _packManagementBusy || _packManagementFeedback.Length > 0;
    public bool IsDatapackOrderDirty => _packOrder is not null && !_packOrder.Items.SequenceEqual(DatapackOrderItems);
    public bool HasDatapackUpdatePreview => _packUpdatePreview is not null;
    public bool HasDatapackUpdateChoices => DatapackUpdateChoices.Count > 0;
    public string DatapackManagementFeedback => _packManagementFeedback;
    public string DatapackOrderWarnings => _packOrder?.Warnings.Count > 0 ? "以下资源存在重叠，改变顺序会改变覆盖结果：\n" + string.Join("\n", _packOrder.Warnings) : "";
    public string DatapackUpdateOrigin => _packUpdateOptions is { } options ? options.Name + " · " + options.Origin : "";
    public string DatapackUpdateSummary => _packUpdatePreview is { } preview ? string.Join("\n", preview.Changes.Concat(preview.Warnings)) : "";
    public bool CanChooseLocalDatapackUpdate => CanManageDatapacks && !IsDatapackOrderDirty && _packUpdateOptions is not null && _packUpdateOpen;
    private bool CanReadPackManagement => WorldWorkspaceCanRead && IsWorldDatapacksPage && Service is IWorldDatapackManagementService;
    private bool CanManageDatapacks => CanReadPackManagement && WorldWorkspaceCanWrite && !_packManagementBusy && !IsWorldSettingsDirty;
    public AsyncRelayCommand ReadDatapackOrderCommand { get; private set; } = null!;
    public RelayCommand MoveDatapackOrderUpCommand { get; private set; } = null!;
    public RelayCommand MoveDatapackOrderDownCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveDatapackOrderCommand { get; private set; } = null!;
    public RelayCommand CancelDatapackManagementCommand { get; private set; } = null!;
    public AsyncRelayCommand ReadDatapackUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand PreviewDatapackUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmDatapackUpdateCommand { get; private set; } = null!;
    private void InitializeDatapackManagement()
    {
        ReadDatapackOrderCommand = AsyncCommand(ReadDatapackOrderAsync, () => CanManageDatapacks && !IsDatapackOrderDirty);
        MoveDatapackOrderUpCommand = Command(() => MoveDatapackOrder(-1), () => CanManageDatapacks && SelectedDatapackOrderItem is { } item && DatapackOrderItems.IndexOf(item) > 0);
        MoveDatapackOrderDownCommand = Command(() => MoveDatapackOrder(1), () => CanManageDatapacks && SelectedDatapackOrderItem is { } item && DatapackOrderItems.IndexOf(item) < DatapackOrderItems.Count - 1);
        SaveDatapackOrderCommand = AsyncCommand(SaveDatapackOrderAsync, () => CanManageDatapacks && IsDatapackOrderDirty && _packManagementKey == PackManagementKey);
        ReadDatapackUpdatesCommand = AsyncCommand(ReadDatapackUpdatesAsync, () => CanManageDatapacks && !IsDatapackOrderDirty && SelectedDatapacks.Count == 1);
        PreviewDatapackUpdateCommand = AsyncCommand(() => PreviewDatapackUpdateAsync(null), () => CanChooseLocalDatapackUpdate && !IsDatapackOrderDirty && SelectedDatapackUpdateChoice is not null);
        ConfirmDatapackUpdateCommand = AsyncCommand(ConfirmDatapackUpdateAsync, () => CanManageDatapacks && !IsDatapackOrderDirty && HasDatapackUpdatePreview && _packManagementKey == PackManagementKey);
        CancelDatapackManagementCommand = Command(() =>
        {
            if (_packManagementBusy && Owner.CancelOperationCommand.CanExecute(null)) Owner.CancelOperationCommand.Execute(null);
            ClearDatapackManagement(); NotifyWorldEditingCommands();
        });
        InitializeDatapackBackups();
    }
    private void ClearDatapackManagement()
    {
        InvalidateDatapackUpdate(); _packOrder = null; _packOrderOpen = _packUpdateOpen = false;
        DatapackOrderItems.Clear(); _selectedPackOrder = null; _packUpdateOptions = null; _packUpdateChoice = null; _updatePackId = null; _packManagementFeedback = "";
        ClearDatapackBackups();
    }
    private void InvalidateDatapackUpdate()
    {
        InvalidateDatapackRestore();
        ++_packManagementRevision; var previous = _packUpdatePreview; _packUpdatePreview = null;
        if (previous is not null) DiscardDatapackPreview(previous.Id);
    }
    private async void DiscardDatapackPreview(string id)
    {
        try { if (Service is IWorldDatapackManagementService service) await service.DiscardWorldDatapackUpdateAsync(id); }
        catch (Exception ex) { Owner.ShowError(UiPrivacy.Redact(ex.Message)); }
    }
    private async Task ReadDatapackOrderAsync()
    {
        if (!CanReadPackManagement || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || Service is not IWorldDatapackManagementService service) return;
        _packManagementKey = PackManagementKey; var key = _packManagementKey; var revision = _packManagementRevision; _packOrderOpen = true; _packManagementBusy = true; _packManagementFeedback = "正在读取加载顺序…"; NotifyDatapackManagement();
        WorldDatapackOrderState? state = null;
        try
        {
            var success = await Owner.RunInlineAsync("读取数据包加载顺序", async ct => state = await service.GetWorldDatapackOrderAsync(instance.Id, world.DirectoryName, ct));
            if (key != PackManagementKey || revision != _packManagementRevision) return;
            if (success && state is not null) { _packOrder = state; DatapackOrderItems.Clear(); foreach (var item in state.Items) DatapackOrderItems.Add(item); _selectedPackOrder = DatapackOrderItems.FirstOrDefault(); }
            _packManagementFeedback = success ? "" : Owner.ErrorMessage ?? "读取已取消，原顺序保留。";
        }
        finally { _packManagementBusy = false; NotifyWorldEditingCommands(); }
    }
    private void MoveDatapackOrder(int delta)
    {
        if (SelectedDatapackOrderItem is not { } item) return; var index = DatapackOrderItems.IndexOf(item); var next = index + delta;
        if (index < 0 || next < 0 || next >= DatapackOrderItems.Count) return; DatapackOrderItems.Move(index, next); InvalidateDatapackUpdate(); NotifyDatapackManagement(); NotifyWorldEditingCommands();
    }
    private async Task SaveDatapackOrderAsync()
    {
        if (_packOrder is not { } order || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || Service is not IWorldDatapackManagementService service) return;
        var key = PackManagementKey; WorldEditReceipt? receipt = null; _packManagementBusy = true; _packManagementFeedback = "正在保存加载顺序…"; NotifyDatapackManagement();
        try { await Owner.RunInlineAsync("保存数据包加载顺序", async ct => receipt = await service.SaveWorldDatapackOrderAsync(instance.Id, world.DirectoryName, order.Revision, DatapackOrderItems.ToArray(), ct)); }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
        if (key != PackManagementKey) return;
        if (receipt is not null) { _packUpdateOpen = false; _packUpdateOptions = null; await RefreshManagedDatapacksAsync(); await ReadDatapackOrderAsync(); _packManagementFeedback = "加载顺序已保存，下次进入存档时生效。"; }
        else _packManagementFeedback = Owner.ErrorMessage ?? "保存已取消，草稿保留。";
        NotifyDatapackManagement();
    }
    private async Task ReadDatapackUpdatesAsync()
    {
        if (SelectedDatapacks.Count != 1 || SelectedDatapacks[0] is not InstanceDatapackRowViewModel selected || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || Service is not IWorldDatapackManagementService service) return;
        InvalidateDatapackUpdate(); _packManagementKey = PackManagementKey; var key = _packManagementKey;
        _updatePackId = "file/" + selected.Name; var id = _updatePackId; _packUpdateOpen = true; _packUpdateOptions = null; _packUpdateChoice = null;
        var revision = _packManagementRevision; _packManagementBusy = true; _packManagementFeedback = "正在检查数据包来源与可用版本…"; NotifyDatapackManagement(); WorldDatapackUpdateOptions? options = null;
        try
        {
            var success = await Owner.RunInlineAsync("检查数据包更新", async ct => options = await service.GetWorldDatapackUpdateOptionsAsync(instance.Id, world.DirectoryName, id, ct));
            if (key != PackManagementKey || revision != _packManagementRevision) return;
            _packUpdateOptions = success ? options : null; _packUpdateChoice = options?.Choices.FirstOrDefault();
            _packManagementFeedback = success ? options?.Notice ?? (options?.ProjectId is not null && options.Choices.Count == 0 ? "没有其它兼容版本。" : "") : Owner.ErrorMessage ?? "检查已取消。";
        }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
    }
    public async Task PreviewLocalDatapackUpdateAsync(string path, string expectedContext, string expectedPackId)
    {
        if (!CanChooseLocalDatapackUpdate || PackManagementKey != expectedContext || _updatePackId != expectedPackId) { Owner.ShowError("目标已改变，未读取替换文件。"); return; }
        await PreviewDatapackUpdateAsync(path);
    }
    public string? DatapackUpdateContext => _packManagementKey;
    public string? DatapackUpdatePackId => _updatePackId;
    private async Task PreviewDatapackUpdateAsync(string? localPath)
    {
        if (_packUpdateOptions is not { } options || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || Service is not IWorldDatapackManagementService service) return;
        if (localPath is null && _packUpdateChoice is null) return;
        InvalidateDatapackUpdate(); var revision = _packManagementRevision; var key = PackManagementKey;
        var request = new WorldDatapackUpdateRequest(instance.Id, world.DirectoryName, options.WorkspaceRevision, options.Selection,
            localPath is null ? _packUpdateChoice!.ReleaseId : null, localPath is null ? _packUpdateChoice!.FileName : null, localPath);
        WorldDatapackUpdatePreview? preview = null; _packManagementBusy = true; _packManagementFeedback = "正在检查兼容性、依赖与冲突…"; NotifyDatapackManagement();
        try
        {
            var success = await Owner.RunInlineAsync("预检数据包更新", async ct => preview = await service.PreviewWorldDatapackUpdateAsync(request, ct));
            if (key != PackManagementKey || revision != _packManagementRevision) { if (preview is not null) DiscardDatapackPreview(preview.Id); return; }
            _packUpdatePreview = success ? preview : null; _packManagementFeedback = success ? "" : Owner.ErrorMessage ?? "预检已取消，原文件保留。";
        }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
    }
    private async Task ConfirmDatapackUpdateAsync()
    {
        if (_packUpdatePreview is not { } preview || Service is not IWorldDatapackManagementService service) return;
        var key = PackManagementKey; WorldDatapackUpdateReceipt? receipt = null;
        _packManagementBusy = true; _packManagementFeedback = "正在更新并备份原数据包…"; NotifyDatapackManagement();
        try { await Owner.RunInlineAsync("更新数据包", async ct => receipt = await service.ApplyWorldDatapackUpdateAsync(preview.Id, ct)); }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
        if (key != PackManagementKey) return;
        InvalidateDatapackUpdate();
        if (receipt is not null) { await RefreshManagedDatapacksAsync(); if (_packOrderOpen) await ReadDatapackOrderAsync(); _packUpdateOpen = false; _packUpdateOptions = null; _packManagementFeedback = "更新已完成，原包及设置已备份至 " + UiPrivacy.Redact(receipt.BackupDirectory); }
        else _packManagementFeedback = Owner.ErrorMessage ?? "更新已取消，原文件保留。";
        NotifyDatapackManagement();
    }
    private async Task RefreshManagedDatapacksAsync()
    {
        CurrentWorldSettings.Phase = WorldReadPhase.Unread; await RefreshWorldPacksAsync();
    }
    private void NotifyDatapackManagement()
    {
        if (ReadDatapackOrderCommand is null) return;
        if (_packManagementKey != PackManagementKey) ClearDatapackManagement();
        if (_packUpdateOpen && (SelectedDatapacks.Count != 1 || SelectedDatapacks[0] is not InstanceDatapackRowViewModel selected || "file/" + selected.Name != _updatePackId)) { InvalidateDatapackUpdate(); _packUpdateOptions = null; _packUpdateOpen = false; }
        OnPropertyChanged(nameof(CanImportWorldDatapack)); OnPropertyChanged(nameof(CanEditWorldMetadata)); OnPropertyChanged(nameof(CanEditWorldDifficulty));
        foreach (var name in new[] { nameof(IsDatapackOrderOpen), nameof(IsDatapackUpdateOpen), nameof(IsDatapackManagementBusy), nameof(ShowDatapackManagementStatus), nameof(IsDatapackOrderDirty), nameof(HasDatapackUpdatePreview), nameof(HasDatapackUpdateChoices), nameof(DatapackManagementFeedback), nameof(DatapackOrderWarnings), nameof(DatapackUpdateOrigin), nameof(DatapackUpdateSummary), nameof(DatapackUpdateChoices), nameof(SelectedDatapackOrderItem), nameof(SelectedDatapackUpdateChoice), nameof(CanChooseLocalDatapackUpdate) }) OnPropertyChanged(name);
        foreach (var command in new IRelayCommand[] { ReadDatapackOrderCommand, MoveDatapackOrderUpCommand, MoveDatapackOrderDownCommand, SaveDatapackOrderCommand, CancelDatapackManagementCommand, ReadDatapackUpdatesCommand, PreviewDatapackUpdateCommand, ConfirmDatapackUpdateCommand }) command.NotifyCanExecuteChanged();
        NotifyDatapackBackups();
    }
}
