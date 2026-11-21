using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private bool _packBackupsOpen;
    private int _packBackupsEpoch;
    private WorldDatapackBackupEntry? _selectedDatapackBackup;
    private WorldDatapackRestorePreview? _datapackRestorePreview;
    public ObservableCollection<WorldDatapackBackupEntry> DatapackBackups { get; } = [];
    public WorldDatapackBackupEntry? SelectedDatapackBackup
    {
        get => _selectedDatapackBackup;
        set { if (SetProperty(ref _selectedDatapackBackup, value)) { InvalidateDatapackRestore(); NotifyDatapackManagement(); } }
    }
    public bool IsDatapackBackupsOpen => _packBackupsOpen;
    public bool HasDatapackRestorePreview => _datapackRestorePreview is not null;
    public string DatapackBackupStatus => SelectedDatapackBackup?.Status ?? (DatapackBackups.Count == 0 ? "暂无更新备份。" : "请选择一条更新备份。");
    public string DatapackRestoreSummary => _datapackRestorePreview is { } preview ? string.Join("\n", preview.Changes.Concat(preview.Warnings)) : "";
    public AsyncRelayCommand ReadDatapackBackupsCommand { get; private set; } = null!;
    public AsyncRelayCommand PreviewDatapackRestoreCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmDatapackRestoreCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenDatapackBackupDirectoryCommand { get; private set; } = null!;
    private bool CanManageDatapackBackups => CanManageDatapacks && !IsDatapackOrderDirty && Service is IWorldDatapackBackupService;
    private void InitializeDatapackBackups()
    {
        ReadDatapackBackupsCommand = AsyncCommand(ReadDatapackBackupsAsync, () => CanManageDatapackBackups);
        PreviewDatapackRestoreCommand = AsyncCommand(PreviewDatapackRestoreAsync, () => CanManageDatapackBackups && SelectedDatapackBackup?.CanRestore == true);
        ConfirmDatapackRestoreCommand = AsyncCommand(ConfirmDatapackRestoreAsync, () => CanManageDatapackBackups && _packManagementKey == PackManagementKey && _datapackRestorePreview is not null);
        OpenDatapackBackupDirectoryCommand = AsyncCommand(async () =>
        {
            if (SelectedInstance is not { } instance || SelectedWorldRow is not { } world || SelectedDatapackBackup is not { } selected || Service is not IWorldDatapackBackupService service) return;
            await Owner.RunControlAsync("打开数据包更新备份", async ct => await Owner.ExternalLauncher.OpenDirectoryAsync(await service.GetWorldDatapackBackupDirectoryAsync(instance.Id, world.DirectoryName, selected.Id, ct), ct));
        }, () => CanReadPackManagement && !Owner.IsBusy && SelectedDatapackBackup is not null && Service is IWorldDatapackBackupService);
    }
    private void InvalidateDatapackRestore()
    {
        ++_packBackupsEpoch; var old = _datapackRestorePreview; _datapackRestorePreview = null; if (old is not null) DiscardDatapackRestore(old.Id);
    }
    private async void DiscardDatapackRestore(string id)
    { try { if (Service is IWorldDatapackBackupService service) await service.DiscardWorldDatapackRestoreAsync(id); } catch (Exception ex) { Owner.ShowError(UiPrivacy.Redact(ex.Message)); } }
    private void ClearDatapackBackups()
    { InvalidateDatapackRestore(); _packBackupsOpen = false; _selectedDatapackBackup = null; DatapackBackups.Clear(); }
    private async Task ReadDatapackBackupsAsync()
    {
        if (!CanManageDatapackBackups || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || Service is not IWorldDatapackBackupService service) return;
        InvalidateDatapackRestore(); InvalidateDatapackUpdate(); _packManagementKey = PackManagementKey; var key = _packManagementKey; var epoch = _packBackupsEpoch;
        _packBackupsOpen = true; _packManagementBusy = true; _packManagementFeedback = "正在读取更新备份…"; NotifyDatapackManagement(); IReadOnlyList<WorldDatapackBackupEntry>? rows = null;
        try
        {
            var success = await Owner.RunInlineAsync("读取数据包更新备份", async ct => rows = await service.GetWorldDatapackBackupsAsync(instance.Id, world.DirectoryName, ct));
            if (key != PackManagementKey || epoch != _packBackupsEpoch) return;
            if (success && rows is not null) { DatapackBackups.Clear(); foreach (var entry in rows) DatapackBackups.Add(entry); _selectedDatapackBackup = DatapackBackups.FirstOrDefault(); }
            _packManagementFeedback = success ? "" : Owner.ErrorMessage ?? "读取已取消。";
        }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
    }
    private async Task PreviewDatapackRestoreAsync()
    {
        if (!CanManageDatapackBackups || SelectedInstance is not { } instance || SelectedWorldRow is not { } world || SelectedDatapackBackup is not { CanRestore: true } selected || Service is not IWorldDatapackBackupService service) return;
        InvalidateDatapackRestore(); InvalidateDatapackUpdate(); var epoch = _packBackupsEpoch; var key = PackManagementKey;
        _packManagementBusy = true; _packManagementFeedback = "正在核对备份与当前文件…"; NotifyDatapackManagement(); WorldDatapackRestorePreview? preview = null;
        try
        {
            var success = await Owner.RunInlineAsync("预检数据包备份恢复", async ct => preview = await service.PreviewWorldDatapackRestoreAsync(instance.Id, world.DirectoryName, selected.Id, ct));
            if (key != PackManagementKey || epoch != _packBackupsEpoch) { if (preview is not null) DiscardDatapackRestore(preview.Id); return; }
            _datapackRestorePreview = success ? preview : null; _packManagementFeedback = success ? "" : Owner.ErrorMessage ?? "检查已取消，文件保留。";
        }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
    }
    private async Task ConfirmDatapackRestoreAsync()
    {
        if (!CanManageDatapackBackups || _datapackRestorePreview is not { } preview || Service is not IWorldDatapackBackupService service) return;
        var key = PackManagementKey; WorldDatapackRestoreReceipt? receipt = null; _packManagementBusy = true; _packManagementFeedback = "正在恢复并保留替换版本…"; NotifyDatapackManagement();
        try { await Owner.RunInlineAsync("恢复数据包更新备份", async ct => receipt = await service.RestoreWorldDatapackBackupAsync(preview.Id, ct)); }
        finally { _packManagementBusy = false; NotifyDatapackManagement(); }
        if (key != PackManagementKey) return;
        InvalidateDatapackRestore();
        if (receipt is not null)
        {
            InvalidateDatapackUpdate(); await RefreshManagedDatapacksAsync(); if (_packOrderOpen) await ReadDatapackOrderAsync(); await ReadDatapackBackupsAsync();
            _packManagementFeedback = "已恢复，原备份和替换版本均保留。";
        }
        else _packManagementFeedback = Owner.ErrorMessage ?? "恢复已取消，文件保留。";
        NotifyDatapackManagement();
    }
    private void NotifyDatapackBackups()
    {
        if (ReadDatapackBackupsCommand is null) return;
        if (IsDatapackOrderDirty && _datapackRestorePreview is not null) InvalidateDatapackRestore();
        foreach (var name in new[] { nameof(IsDatapackBackupsOpen), nameof(HasDatapackRestorePreview), nameof(DatapackBackupStatus), nameof(DatapackRestoreSummary), nameof(SelectedDatapackBackup) }) OnPropertyChanged(name);
        foreach (var command in new IRelayCommand[] { ReadDatapackBackupsCommand, PreviewDatapackRestoreCommand, ConfirmDatapackRestoreCommand, OpenDatapackBackupDirectoryCommand }) command.NotifyCanExecuteChanged();
    }
}
