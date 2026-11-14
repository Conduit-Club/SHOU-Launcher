using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private string _localWorldPath = "", _localWorldFolder = "", _localWorldStatus = "";
    private string? _localWorldContext;
    private LocalWorldImportPreview? _localWorldPreview;
    private int _localWorldRevision;
    private bool _localWorldBusy;
    private string? LocalWorldContextKey => SelectedInstance is { } instance && instance.Id == Owner.Snapshot.SelectedInstanceId ? instance.Id + "\0" + instance.GameDirectory : null;
    public bool CanSelectLocalWorldArchive => WorldWorkspaceCanWrite && IsWorldListPage && ReferenceEquals(Owner.SelectedPage, this) && Service is ILocalWorldImportService && !_localWorldBusy;
    public bool IsLocalWorldImportOpen => _localWorldPath.Length > 0;
    public bool IsLocalWorldImportBusy => _localWorldBusy;
    public bool HasLocalWorldPreview => _localWorldPreview is not null;
    public string LocalWorldArchiveName => Path.GetFileName(_localWorldPath);
    public string LocalWorldDirectoryName { get => _localWorldFolder; set { if (SetProperty(ref _localWorldFolder, value)) { InvalidateLocalWorldPreview(); _localWorldStatus = ""; NotifyLocalWorldImport(); } } }
    public string LocalWorldImportStatus => _localWorldStatus;
    public string LocalWorldPreviewSummary => _localWorldPreview is { } p ? $"{p.WorldName}\n{p.Installation.GameVersion} · {p.Installation.Files.Count} 个文件\n目标：{p.Installation.InstanceName} / {p.Installation.WorldDirectoryName}" : "";
    public AsyncRelayCommand PreviewLocalWorldImportCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmLocalWorldImportCommand { get; private set; } = null!;
    public RelayCommand CancelLocalWorldImportCommand { get; private set; } = null!;
    private void InitializeLocalWorldImport()
    {
        PreviewLocalWorldImportCommand = AsyncCommand(PreviewLocalWorldImportAsync, () => CanSelectLocalWorldArchive && IsLocalWorldImportOpen && !string.IsNullOrWhiteSpace(LocalWorldDirectoryName));
        ConfirmLocalWorldImportCommand = AsyncCommand(ConfirmLocalWorldImportAsync, () => CanSelectLocalWorldArchive && HasLocalWorldPreview && _localWorldContext == LocalWorldContextKey);
        CancelLocalWorldImportCommand = Command(() =>
        {
            if (_localWorldBusy && Owner.CancelOperationCommand.CanExecute(null)) Owner.CancelOperationCommand.Execute(null);
            InvalidateLocalWorldPreview(); _localWorldPath = ""; _localWorldStatus = ""; NotifyLocalWorldImport();
        }, () => IsLocalWorldImportOpen);
    }
    public bool SelectLocalWorldArchive(IReadOnlyList<string> paths, string expectedInstanceId, string expectedGameDirectory)
    {
        if (!CanSelectLocalWorldArchive || SelectedInstance is not { } instance || instance.Id != expectedInstanceId || instance.GameDirectory != expectedGameDirectory)
        { Owner.ShowError("目标实例已经改变，未读取或导入文件。"); return false; }
        if (paths.Count != 1 || !Path.IsPathFullyQualified(paths[0]) || !Path.GetExtension(paths[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        { Owner.ShowError("请选择或复制一个本地 Java 世界 ZIP 文件。"); return false; }
        InvalidateLocalWorldPreview(); _localWorldContext = LocalWorldContextKey; _localWorldPath = paths[0];
        var name = Path.GetFileNameWithoutExtension(paths[0]);
        _localWorldFolder = new string(name.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).Take(120).ToArray()).Trim().Trim('.');
        if (_localWorldFolder.Length == 0 || _localWorldFolder.StartsWith('.')) _localWorldFolder = "导入的世界";
        _localWorldStatus = ""; NotifyLocalWorldImport(); return true;
    }
    private void InvalidateLocalWorldPreview()
    {
        ++_localWorldRevision; var previous = _localWorldPreview; _localWorldPreview = null;
        if (previous is not null) DiscardLocalWorldPreview(previous.Installation.Id);
    }
    private async void DiscardLocalWorldPreview(string id)
    {
        try { if (Service is ILocalWorldImportService service) await service.DiscardLocalWorldImportAsync(id); }
        catch (Exception ex) { Owner.ShowError("预检释放失败：" + UiPrivacy.Redact(ex.Message)); }
    }
    private async Task PreviewLocalWorldImportAsync()
    {
        if (!CanSelectLocalWorldArchive || SelectedInstance is not { } instance || Service is not ILocalWorldImportService service) return;
        InvalidateLocalWorldPreview(); var revision = _localWorldRevision; var key = LocalWorldContextKey;
        var request = new LocalWorldImportRequest(instance.Id, _localWorldPath, LocalWorldDirectoryName.Trim());
        LocalWorldImportPreview? preview = null; _localWorldBusy = true; _localWorldStatus = "正在检查世界与版本…"; NotifyLocalWorldImport();
        try
        {
            var success = await Owner.RunInlineAsync("检查本地世界", async ct => preview = await service.PreviewLocalWorldImportAsync(request, ct));
            if (revision != _localWorldRevision || key != LocalWorldContextKey)
            { if (preview is not null) DiscardLocalWorldPreview(preview.Installation.Id); return; }
            _localWorldPreview = success ? preview : null; _localWorldStatus = success ? "" : Owner.ErrorMessage ?? "检查已取消，未写入存档。";
        }
        finally { _localWorldBusy = false; NotifyLocalWorldImport(); }
    }
    private async Task ConfirmLocalWorldImportAsync()
    {
        if (!CanSelectLocalWorldArchive || _localWorldPreview is not { } preview || _localWorldContext != LocalWorldContextKey || Service is not ILocalWorldImportService service) return;
        var key = LocalWorldContextKey; var revision = _localWorldRevision; var sourcePath = _localWorldPath; WorldContentResult? imported = null;
        _localWorldBusy = true; _localWorldStatus = "正在导入世界…"; NotifyLocalWorldImport();
        try
        {
            var success = await Owner.RunInlineAsync("导入本地世界", async ct => imported = await service.ImportLocalWorldAsync(preview.Installation.Id, ct));
            if (key != LocalWorldContextKey) return;
            if (revision != _localWorldRevision)
            {
                if (imported is not null)
                {
                    _localWorldPath = sourcePath; _localWorldContext = key; _localWorldStatus = "世界已完成导入，取消请求未移除已完成的存档。";
                    await RefreshWorldsAsync();
                }
                return;
            }
            InvalidateLocalWorldPreview(); _localWorldStatus = imported is not null ? $"已导入 {imported.WorldDirectoryName}。" : success ? "导入未返回结果，请重新读取核对。" : Owner.ErrorMessage ?? "导入已取消，原存档保留。";
            if (imported is not null) await RefreshWorldsAsync();
        }
        finally { _localWorldBusy = false; NotifyLocalWorldImport(); }
    }
    private void NotifyLocalWorldImport()
    {
        if (PreviewLocalWorldImportCommand is null) return;
        if (_localWorldContext != LocalWorldContextKey && IsLocalWorldImportOpen)
        { InvalidateLocalWorldPreview(); _localWorldPath = ""; _localWorldStatus = ""; }
        foreach (var name in new[] { nameof(CanSelectLocalWorldArchive), nameof(IsLocalWorldImportOpen), nameof(IsLocalWorldImportBusy), nameof(HasLocalWorldPreview), nameof(LocalWorldArchiveName), nameof(LocalWorldDirectoryName), nameof(LocalWorldImportStatus), nameof(LocalWorldPreviewSummary) }) OnPropertyChanged(name);
        PreviewLocalWorldImportCommand.NotifyCanExecuteChanged(); ConfirmLocalWorldImportCommand.NotifyCanExecuteChanged(); CancelLocalWorldImportCommand.NotifyCanExecuteChanged();
    }
}
