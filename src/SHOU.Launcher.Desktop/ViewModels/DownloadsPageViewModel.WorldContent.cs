using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    private IWorldContentInstallationService? _worldContent;
    private WorldContentTarget? _selectedWorldTarget;
    private WorldContentPreview? _worldPreview;
    private string _worldFolder = "", _worldInstallStatus = "";
    private string? _loadedWorldInstance;
    private string? _worldStatusProject;
    private int _worldReadRevision, _worldPreviewRevision;
    private CancellationTokenSource? _worldRead;
    private (string Instance, string GameDirectory, string World)? _worldBrowserContext;
    public void OpenWorldContentBrowser(GameInstance instance, string? worldDirectoryName = null)
    {
        var target = ContentInstallTargets.FirstOrDefault(t => t.Instance.Id == instance.Id && t.Instance.GameDirectory == instance.GameDirectory)
            ?? throw new InvalidOperationException("目标实例已经改变，请重新选择。");
        _worldBrowserContext = null; _worldRead?.Cancel(); ++_worldReadRevision; _loadedWorldInstance = null;
        WorldContentTargets.Clear(); SelectedWorldContentTarget = null; SelectedInstallTarget = target;
        if (worldDirectoryName is not null) _worldBrowserContext = (instance.Id, instance.GameDirectory, worldDirectoryName);
        _isConfiguration = false; _isResourceDetail = false; _isPackageDetail = false;
        SelectCategory(worldDirectoryName is null ? "world" : "datapack");
    }
    private void WorldInstallTargetChanged()
    {
        if (_worldBrowserContext is { } context && (SelectedInstallTarget?.Instance.Id != context.Instance || SelectedInstallTarget.Instance.GameDirectory != context.GameDirectory)) _worldBrowserContext = null;
        _worldRead?.Cancel(); ++_worldReadRevision; _loadedWorldInstance = null;
        WorldContentTargets.Clear(); SelectedWorldContentTarget = null;
        InvalidateWorldPreview(); RefreshWorldTargets();
    }
    public bool ShowWorldContentInstall => IsResourceDetail && SelectedProject?.Kind is "datapack" or "world";
    public bool IsDatapackInstall => ShowWorldContentInstall && SelectedProject?.Kind == "datapack";
    public bool IsWorldDownloadInstall => ShowWorldContentInstall && SelectedProject?.Kind == "world";
    public ObservableCollection<WorldContentTarget> WorldContentTargets { get; } = [];
    public WorldContentTarget? SelectedWorldContentTarget { get => _selectedWorldTarget; set { if (SetProperty(ref _selectedWorldTarget, value)) { InvalidateWorldPreview(); NotifyCommands(); } } }
    public string NewWorldDirectoryName { get => _worldFolder; set { if (SetProperty(ref _worldFolder, value)) { InvalidateWorldPreview(); NotifyCommands(); } } }
    public string WorldContentStatus { get => _worldInstallStatus; private set => SetProperty(ref _worldInstallStatus, value); }
    public bool HasWorldContentPreview => _worldPreview is not null;
    public string WorldContentPreviewText => _worldPreview is { } p ? $"目标：{p.InstanceName} / {p.WorldDirectoryName} · {p.GameVersion}\n{p.Files.Count} 个文件\n" + string.Join("\n", p.Warnings) : "";
    public AsyncRelayCommand ConfirmWorldContentInstallCommand { get; private set; } = null!;
    public RelayCommand CancelWorldContentPreviewCommand { get; private set; } = null!;
    public RelayCommand RefreshWorldContentTargetsCommand { get; private set; } = null!;
    private void InitializeWorldContentInstallation()
    {
        _worldContent = Service as IWorldContentInstallationService;
        ConfirmWorldContentInstallCommand = AsyncCommand(ConfirmWorldContentInstallAsync, () => CanEdit && _worldPreview is not null);
        CancelWorldContentPreviewCommand = Command(() => InvalidateWorldPreview(), () => CanEdit && HasWorldContentPreview);
        RefreshWorldContentTargetsCommand = Command(() => { _loadedWorldInstance = null; RefreshWorldTargets(); }, () => CanEdit && IsDatapackInstall && SelectedInstallTarget is not null);
    }
    private void InvalidateWorldPreview()
    {
        ++_worldPreviewRevision; _worldPreview = null;
        OnPropertyChanged(nameof(HasWorldContentPreview)); OnPropertyChanged(nameof(WorldContentPreviewText));
        ConfirmWorldContentInstallCommand?.NotifyCanExecuteChanged(); CancelWorldContentPreviewCommand?.NotifyCanExecuteChanged();
    }
    private async void RefreshWorldTargets()
    {
        if (_worldContent is null || !IsDatapackInstall || SelectedInstallTarget is not { } target || _loadedWorldInstance == target.Instance.Id) return;
        _worldRead?.Cancel(); _worldRead?.Dispose(); _worldRead = CancellationTokenSource.CreateLinkedTokenSource(_imageToken);
        var token = _worldRead.Token; var revision = ++_worldReadRevision;
        _loadedWorldInstance = target.Instance.Id; WorldContentTargets.Clear(); SelectedWorldContentTarget = null;
        try
        {
            var worlds = await _worldContent.GetWorldContentTargetsAsync(target.Instance.Id, token);
            if (revision != _worldReadRevision || _disposed || !IsDatapackInstall || SelectedInstallTarget?.Instance.Id != target.Instance.Id) return;
            foreach (var world in worlds) WorldContentTargets.Add(world);
            if (_worldBrowserContext is { } context && target.Instance.Id == context.Instance && target.Instance.GameDirectory == context.GameDirectory)
            {
                SelectedWorldContentTarget = worlds.FirstOrDefault(w => w.DirectoryName == context.World && w.Error is null);
                _worldBrowserContext = null;
                if (SelectedWorldContentTarget is null) WorldContentStatus = "此前选择的存档已不可用，请重新选择目标。";
            }
            if (worlds.Count == 0) WorldContentStatus = "此实例没有可选存档。";
            else if (WorldContentStatus.StartsWith("此实例没有", StringComparison.Ordinal) || WorldContentStatus.StartsWith("读取存档失败", StringComparison.Ordinal)) WorldContentStatus = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (revision == _worldReadRevision && !_disposed) { _loadedWorldInstance = null; WorldContentStatus = "读取存档失败：" + UiPrivacy.Redact(ex.Message); } }
        finally { if (revision == _worldReadRevision && !_disposed) NotifyCommands(); }
    }
    private bool CanPreviewWorldResource(ContentRelease? release) => CanEdit && _worldContent is not null && ShowWorldContentInstall &&
        release is not null && SelectedInstallTarget is { } target && release.GameVersions.Contains(target.GameVersion, StringComparer.Ordinal) &&
        (IsDatapackInstall ? SelectedWorldContentTarget is { Error: null } : !string.IsNullOrWhiteSpace(NewWorldDirectoryName));
    private async Task PreviewWorldResourceAsync(ContentRelease release, ContentFile file)
    {
        if (!CanPreviewWorldResource(release) || SelectedProject is not { } project || SelectedInstallTarget is not { } target) return;
        InvalidateWorldPreview(); var revision = _worldPreviewRevision;
        var world = IsDatapackInstall ? SelectedWorldContentTarget!.DirectoryName : NewWorldDirectoryName.Trim();
        WorldContentPreview? preview = null; WorldContentStatus = "正在下载并检查兼容性与冲突…";
        var success = await Owner.RunAsync("预检存档资源", async ct => preview = await _worldContent!.PreviewWorldContentAsync(new(target.Instance.Id, project.Id, release.Id, file.FileName, project.Kind, world), ct));
        if (revision != _worldPreviewRevision || SelectedProject?.Id != project.Id) return;
        _worldPreview = success ? preview : null; WorldContentStatus = success ? "" : Owner.ErrorMessage ?? "预检已取消。";
        OnPropertyChanged(nameof(HasWorldContentPreview)); OnPropertyChanged(nameof(WorldContentPreviewText)); NotifyCommands();
    }
    private async Task ConfirmWorldContentInstallAsync()
    {
        if (_worldPreview is not { } preview || _worldContent is null) return;
        var revision = _worldPreviewRevision; WorldContentResult? installed = null;
        var success = await Owner.RunAsync("安装存档资源", async ct => installed = await _worldContent.InstallWorldContentAsync(preview.Id, ct));
        if (revision != _worldPreviewRevision) return;
        InvalidateWorldPreview();
        WorldContentStatus = success ? $"已安装到 {preview.InstanceName} / {installed!.WorldDirectoryName}，新增 {installed.AddedFiles} 个文件。" : Owner.ErrorMessage ?? "安装已取消，原存档保留。";
        _loadedWorldInstance = null; RefreshWorldTargets();
    }
    private void NotifyWorldContentPage()
    {
        if (_worldStatusProject != SelectedProject?.Id) { _worldStatusProject = SelectedProject?.Id; WorldContentStatus = ""; }
        InvalidateWorldPreview();
        foreach (var name in new[] { nameof(ShowWorldContentInstall), nameof(IsDatapackInstall), nameof(IsWorldDownloadInstall) }) OnPropertyChanged(name);
        if (IsWorldDownloadInstall && string.IsNullOrWhiteSpace(NewWorldDirectoryName)) NewWorldDirectoryName = SelectedProject?.Slug ?? "";
        RefreshWorldTargets();
    }
}
