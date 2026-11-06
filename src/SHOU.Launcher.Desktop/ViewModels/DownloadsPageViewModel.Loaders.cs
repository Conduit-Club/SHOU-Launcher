using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class AdditionalLoaderCatalogViewModel(DownloadsPageViewModel owner, string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public ObservableCollection<LoaderVersionChoice> Versions { get; } = [];
    public LoaderVersionChoice? SelectedVersion { get => owner.SelectedAdditionalLoader?.Loader == Id ? owner.SelectedAdditionalLoader : null; set => owner.SelectAdditionalLoader(Id, value); }
    private string _status = "";
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public bool CanEdit => owner.CanEdit;
    public AsyncRelayCommand RefreshCommand { get; } = new(() => owner.RefreshAdditionalLoaderAsync(id), () => owner.CanReadAdditionalLoader);
    public RelayCommand ClearCommand { get; } = new(() => owner.SelectAdditionalLoader(id, null), () => owner.CanEdit && owner.SelectedAdditionalLoader?.Loader == id);
    internal void NotifyState()
    {
        OnPropertyChanged(nameof(SelectedVersion)); OnPropertyChanged(nameof(CanEdit));
        RefreshCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class DownloadsPageViewModel
{
    private ILoaderInstanceService? _loaderService;
    private LoaderVersionChoice? _selectedAdditionalLoader;
    public IReadOnlyList<AdditionalLoaderCatalogViewModel> AdditionalLoaders { get; private set; } = [];
    public LoaderVersionChoice? SelectedAdditionalLoader => _selectedAdditionalLoader;
    public string AdditionalLoaderCapability => _loaderService is null ? "当前后端尚未提供Forge / NeoForge / Quilt安装能力。"
        : "Forge / NeoForge / Quilt当前开放已验证的Minecraft 1.21.1安装；其它版本可读取目录，安装暂不可用。";
    internal bool CanReadAdditionalLoader => _loaderService is not null && CanQuery && _selectedVersion is { CanInstall: true } && IsConfiguration;
    private bool ValidAdditionalLoader => _selectedAdditionalLoader is null || _loaderService is not null && SelectedVersionId == "1.21.1" && _selectedAdditionalLoader.CanInstall;
    private string AdditionalLoaderValidation => _selectedAdditionalLoader is null ? ""
        : _loaderService is null ? "当前后端未提供所选加载器安装能力。"
        : SelectedVersionId != "1.21.1" ? "当前已验证的Forge / NeoForge / Quilt安装范围为Minecraft 1.21.1；此版本暂不可安装。"
        : !_selectedAdditionalLoader.CanInstall ? _selectedAdditionalLoader.Detail ?? "此加载器版本的安装链尚未支持。" : "";
    private static string LoaderTitle(string loader) => loader switch { "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt", _ => loader };
    private void InitializeAdditionalLoaders(ILoaderInstanceService? service)
    {
        _loaderService = service ?? Service as ILoaderInstanceService;
        AdditionalLoaders = [new(this, "forge", "Forge"), new(this, "neoforge", "NeoForge"), new(this, "quilt", "Quilt")];
    }
    private void ClearAdditionalSelection()
    {
        _selectedAdditionalLoader = null; OnPropertyChanged(nameof(SelectedAdditionalLoader));
        foreach (var catalog in AdditionalLoaders) catalog.NotifyState();
    }
    internal void SelectAdditionalLoader(string loader, LoaderVersionChoice? version, bool internalUpdate = false)
    {
        if (!internalUpdate && !CanEdit || version is null && _selectedAdditionalLoader?.Loader != loader || version is not null && version.Loader != loader) return;
        if (Equals(_selectedAdditionalLoader, version)) return;
        var previousDefault = DefaultInstallationName;
        _selectedAdditionalLoader = version;
        if (version is not null) { _selectedFabric = null; OnPropertyChanged(nameof(SelectedFabric)); }
        OnPropertyChanged(nameof(SelectedAdditionalLoader));
        foreach (var catalog in AdditionalLoaders) catalog.NotifyState();
        if (InstallationName == previousDefault) InstallationName = DefaultInstallationName;
        NotifyInstallDraft();
    }
    internal async Task RefreshAdditionalLoaderAsync(string loader)
    {
        if (!CanReadAdditionalLoader || _loaderService is null) return;
        var catalog = AdditionalLoaders.Single(c => c.Id == loader); var game = SelectedVersionId; var revision = _requestRevision;
        var previous = catalog.SelectedVersion?.Version;
        catalog.Status = "正在读取" + catalog.Title + "官方目录…";
        var success = await QueryAsync("读取" + catalog.Title + "目录", revision, async ct =>
        {
            var versions = await _loaderService.GetLoaderVersionsAsync(loader, game, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision || !IsConfiguration) return;
            if (versions.Count > 2000 || versions.Any(v => v.Loader != loader || string.IsNullOrWhiteSpace(v.Version))) throw new InvalidDataException("加载器目录身份或数量无效。");
            catalog.Versions.Clear(); foreach (var version in versions.DistinctBy(v => v.Version)) catalog.Versions.Add(version);
            if (_selectedAdditionalLoader?.Loader == loader)
            {
                var next = catalog.Versions.FirstOrDefault(v => v.Version == previous);
                if (next is null)
                {
                    next = _selectedAdditionalLoader with { CanInstall = false, Detail = "此前选择已不在最新目录，请重新选择或明确使用原版。" };
                    catalog.Versions.Insert(0, next);
                }
                SelectAdditionalLoader(loader, next, internalUpdate: true);
            }
            catalog.Status = versions.Count == 0 ? "此 Minecraft 版本没有可用的 " + catalog.Title + " 构建。"
                : game == "1.21.1" ? "" : "此 Minecraft 版本暂不支持安装此加载器。";
        });
        if (revision == _requestRevision)
        { if (!success) catalog.Status = RequestStatus; else SetRequestStatus("", false); }
    }
    private void ResetAdditionalLoaders()
    {
        ClearAdditionalSelection();
        foreach (var catalog in AdditionalLoaders) { catalog.Versions.Clear(); catalog.Status = ""; }
    }
    private void ClearAllLoaders()
    {
        SelectedFabric = null;
        if (_selectedAdditionalLoader is { } selected) SelectAdditionalLoader(selected.Loader, null);
    }
}
