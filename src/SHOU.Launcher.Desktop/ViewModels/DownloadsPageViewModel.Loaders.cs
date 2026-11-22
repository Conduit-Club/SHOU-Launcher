using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class AdditionalLoaderCatalogViewModel(DownloadsPageViewModel owner, string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public Avalonia.Media.IImage Icon => LoaderArtwork.For(Id);
    public ObservableCollection<LoaderVersionChoice> Versions { get; } = [];
    public LoaderVersionChoice? SelectedVersion { get => owner.SelectedAdditionalLoader?.Loader == Id ? owner.SelectedAdditionalLoader : null; set => owner.SelectAdditionalLoader(Id, value); }
    private string _status = "";
    private bool _isLoading;
    public bool Loaded { get; internal set; }
    public bool IsLoading { get => _isLoading; internal set { if (SetProperty(ref _isLoading, value)) NotifyState(); } }
    public string Summary => SelectedVersion?.DisplayVersion ?? (IsLoading ? "正在获取版本…" : Versions.Count > 0 ? Versions.Any(v => v.CanInstall) ? "可以添加" : "仅浏览" : Loaded ? "无可用版本" : Status.Length > 0 ? "读取失败" : "");
    public bool HasSelection => SelectedVersion is not null;
    public string Status { get => _status; internal set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(Summary)); } }
    public bool CanEdit => owner.CanEdit;
    public AsyncRelayCommand RefreshCommand { get; } = new(() => owner.RefreshAdditionalLoaderAsync(id), () => owner.CanReadAdditionalLoader);
    public RelayCommand ClearCommand { get; } = new(() => owner.SelectAdditionalLoader(id, null), () => owner.CanEdit && owner.SelectedAdditionalLoader?.Loader == id);
    internal void NotifyState()
    {
        OnPropertyChanged(nameof(SelectedVersion)); OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        RefreshCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class DownloadsPageViewModel
{
    private ILoaderInstanceService? _loaderService;
    private bool _fabricLoading, _fabricLoaded;
    public bool FabricLoading { get => _fabricLoading; private set { if (SetProperty(ref _fabricLoading, value)) OnPropertyChanged(nameof(FabricSummary)); } }
    public string FabricSummary => SelectedFabric?.Version ?? (FabricLoading ? "正在获取版本…" : FabricVersions.Count > 0 ? "可以添加" : _fabricLoaded ? "无可用版本" : FabricStatus.Length > 0 ? "读取失败" : "");
    private void StartAutomaticLoaders()
    {
        if (!IsConfiguration || !CanQuery) return;
        if (!_fabricLoaded && !FabricLoading) _ = RefreshFabricAsync();
        foreach (var loader in AdditionalLoaders.Where(x => !x.Loaded && !x.IsLoading)) _ = RefreshAdditionalLoaderAsync(loader.Id);
    }
    private LoaderVersionChoice? _selectedAdditionalLoader;
    public IReadOnlyList<AdditionalLoaderCatalogViewModel> AdditionalLoaders { get; private set; } = [];
    public LoaderVersionChoice? SelectedAdditionalLoader => _selectedAdditionalLoader;
    public string AdditionalLoaderCapability => _loaderService is null ? "当前后端尚未提供Forge / NeoForge / Quilt安装能力。"
        : "选择目录中受支持的加载器版本进行安装。";
    internal bool CanReadAdditionalLoader => _loaderService is not null && CanQuery && _selectedVersion is { CanInstall: true } && IsConfiguration;
    private bool ValidAdditionalLoader => _selectedAdditionalLoader is null || _loaderService is not null && _selectedAdditionalLoader.CanInstall;
    private string AdditionalLoaderValidation => _selectedAdditionalLoader is null ? ""
        : _loaderService is null ? "当前后端未提供所选加载器安装能力。"
        : !_selectedAdditionalLoader.CanInstall ? _selectedAdditionalLoader.Detail ?? "此加载器版本的安装链尚未支持。" : "";
    private static string LoaderTitle(string loader) => loader switch { "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt", "cleanroom" => "Cleanroom", "legacyfabric" => "Legacy Fabric", "optifine" => "OptiFine", "labymod" => "LabyMod", "liteloader" => "LiteLoader", _ => loader };
    private void InitializeAdditionalLoaders(ILoaderInstanceService? service)
    {
        _loaderService = service ?? Service as ILoaderInstanceService;
        AdditionalLoaders = [new(this, "forge", "Forge"), new(this, "neoforge", "NeoForge"), new(this, "quilt", "Quilt"), new(this, "cleanroom", "Cleanroom"), new(this, "legacyfabric", "Legacy Fabric"), new(this, "optifine", "OptiFine"), new(this, "labymod", "LabyMod"), new(this, "liteloader", "LiteLoader")];
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
        if (catalog.IsLoading) return;
        var previous = catalog.SelectedVersion?.Version;
        catalog.IsLoading = true; catalog.Status = "";
        try { await ReadBrowseAsync("获取 " + catalog.Title + " 版本", revision, async ct =>
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
                : versions.Any(v => v.CanInstall) ? "" : "此 Minecraft 版本暂不支持安装此加载器。";
            catalog.Loaded = true;
        }, "loader-" + loader, page: false, failure: message => catalog.Status = message); }
        finally { if (revision == _requestRevision) { catalog.IsLoading = false; catalog.NotifyState(); } }
    }
    private void ResetAdditionalLoaders()
    {
        ClearAdditionalSelection();
        foreach (var catalog in AdditionalLoaders) { catalog.Versions.Clear(); catalog.Loaded = false; catalog.IsLoading = false; catalog.Status = ""; }
        _fabricLoaded = false; FabricLoading = false;
    }
    private void ClearAllLoaders()
    {
        SelectedFabric = null;
        if (_selectedAdditionalLoader is { } selected) SelectAdditionalLoader(selected.Loader, null);
    }
}
