using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Themes;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class DownloadRowViewModel : ObservableObject
{
    public DownloadTaskSnapshot Task { get; private set; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public string Name => Task.Name;
    public string State => Task.State switch { "Queued" => "等待中", "Downloading" => "下载中", "Completed" => "已完成", "Failed" => "失败", "Cancelled" => "已取消", _ => Task.State };
    public bool IsIndeterminate => Task.TotalBytes is null or <= 0;
    public double Progress => Task.TotalBytes is > 0 ? Math.Clamp(Task.BytesReceived * 100d / Task.TotalBytes.Value, 0, 100) : 0;
    public string Transfer => $"{FormatBytes(Task.BytesReceived)} / {(Task.TotalBytes is { } total ? FormatBytes(total) : "未知大小")} · {FormatBytes((long)Task.BytesPerSecond)}/s";
    public string Source => Task.Source is { } source ? UiPrivacy.Redact(source) : "来源尚未确定";
    public string? Error => Task.Error is { } error ? UiPrivacy.Redact(error) : null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public DownloadRowViewModel(MainWindowViewModel owner, DownloadTaskSnapshot task)
    {
        Task = task;
        CancelCommand = new(() => owner.RunControlAsync("取消所属安装", ct => owner.Service.CancelDownloadAsync(Task.Id, ct)), () => Task.State is "Queued" or "Downloading");
        RetryCommand = new(() => owner.RunAsync("重试所属安装", ct => owner.Service.RetryDownloadAsync(Task.Id, ct)), () => owner.CanMutate && Task.State is "Failed" or "Cancelled");
    }
    internal void Update(DownloadTaskSnapshot task)
    {
        if (Task == task) return;
        Task = task;
        foreach (var property in new[] { nameof(Task), nameof(Name), nameof(State), nameof(IsIndeterminate), nameof(Progress), nameof(Transfer), nameof(Source), nameof(Error), nameof(HasError) }) OnPropertyChanged(property);
        CancelCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
    }
    private static string FormatBytes(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
}

public sealed class DownloadCategoryViewModel(DownloadsPageViewModel owner, string id, string title, string icon)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string IconPath { get; } = icon;
    public bool IsSelected => owner.SelectedCategory.Id == Id;
    public RelayCommand SelectCommand { get; } = new(() => owner.SelectCategory(id));
    internal void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));
}

public sealed class GameCatalogRowViewModel
{
    public GameCatalogVersion Version { get; }
    public string Id => Version.Id;
    public string Detail => !Version.CanInstall ? "此历史版本标识暂不支持安装。"
        : (Version.Latest ? (Version.Kind == "release" ? "最新正式版，" : "最新预览版，") : "")
            + (Version.ReleaseTime == DateTimeOffset.MinValue ? "未提供发布时间" : $"发布于 {Version.ReleaseTime.LocalDateTime:yyyy/M/d HH:mm}");
    public RelayCommand SelectCommand { get; }
    public GameCatalogRowViewModel(DownloadsPageViewModel owner, GameCatalogVersion version)
    {
        Version = version;
        SelectCommand = new(() => owner.SelectVersion(version), () => owner.CanEdit && version.CanInstall);
    }
}

public sealed record GameCatalogGroupViewModel(string Name, IReadOnlyList<GameCatalogRowViewModel> Versions)
{
    public string Header => $"{Name} ({Versions.Count})";
}
public sealed record ContentSortChoice(string Id, string Name);

public sealed class ContentProjectRowViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private Avalonia.Media.Imaging.Bitmap? _icon;
    public Avalonia.Media.Imaging.Bitmap? Icon { get => _icon; private set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => Icon is not null;
    public ContentProject Project { get; }
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string Tile => Title.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(Title);
    public string Detail => $"{Project.Author}  ·  下载 {Project.Downloads:N0} 次  ·  Modrinth";
    public AsyncRelayCommand OpenCommand { get; }
    public ContentProjectRowViewModel(DownloadsPageViewModel owner, ContentProject project)
    {
        Project = project;
        OpenCommand = new(() => owner.OpenProjectAsync(project), () => owner.CanQuery);
    }
    internal async Task LoadIconAsync(ILauncherCatalogService catalog, SemaphoreSlim gate, CancellationToken ct)
    {
        if (_disposed || Icon is not null || string.IsNullOrWhiteSpace(Project.IconUrl)) return;
        await gate.WaitAsync(ct);
        try
        {
            var bytes = await catalog.GetContentIconAsync(Project.IconUrl, ct); ct.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 40);
            if (_disposed) bitmap.Dispose(); else Icon = bitmap;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* A missing or invalid thumbnail keeps the text fallback. */ }
        finally { gate.Release(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Icon?.Dispose(); Icon = null; }
}

public sealed class ContentFileRowViewModel
{
    public ContentFile File { get; }
    public string Name => File.FileName;
    public string Detail => $"{File.Size / 1048576d:0.00} MiB · SHA-1 {File.Sha1[..Math.Min(12, File.Sha1.Length)]}…";
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public bool ShowInstallButton { get; }
    public ContentFileRowViewModel(DownloadsPageViewModel owner, ContentFile file, ContentRelease? release = null)
    {
        File = file;
        DownloadCommand = new(() => owner.DownloadFileAsync(file), () => owner.CanEdit && owner.CatalogAvailable);
        InstallCommand = new(() => owner.InstallResourceAsync(release!, file), () => owner.CanInstallResource(release));
        ShowInstallButton = owner.ShowResourceInstall;
    }
}
public sealed record ContentReleaseRowViewModel(ContentRelease Release, IReadOnlyList<ContentFileRowViewModel> Files)
{
    public string Name => Release.Name;
    public string Detail => $"{Release.Version} · {Release.Published.LocalDateTime:yyyy/M/d HH:mm}" +
        (Release.Environment is "server_only" or "server_only_client_optional" or "dedicated_server_only" ? " · 服务器专用，客户端不支持安装" : "");
}
public sealed record ContentReleaseGroupViewModel(string Header, IReadOnlyList<ContentReleaseRowViewModel> Releases);

public sealed partial class DownloadsPageViewModel : PageViewModel, IDisposable
{
    private readonly ILauncherCatalogService? _catalog;
    private readonly CancellationTokenSource _imageLifetime = new();
    private readonly CancellationToken _imageToken;
    private readonly SemaphoreSlim _imageGate = new(4, 4);
    private bool _disposed;
    private readonly string? _favoritesPathOverride;
    private readonly List<ContentProject> _favorites = [];
    private DownloadCategoryViewModel _selectedCategory = null!;
    private bool _isConfiguration;
    private bool _isResourceDetail;
    private bool _loading;
    private bool _catalogLoaded;
    private bool _searchCompleted;
    private int _requestRevision;
    private int _searchOffset;
    private string _queryText = "";
    private string _gameVersionFilter = "";
    private string _loaderFilter = "任意";
    private ContentSortChoice _selectedSort = null!;
    private string _requestStatus = "点击刷新读取 Minecraft 版本目录。";
    private string _catalogSource = "";
    private GameCatalogVersion? _selectedVersion;
    private LoaderCatalogVersion? _selectedFabric;
    private string _installationName = "";
    private string _fabricStatus = "展开后点击读取，查询所选 Minecraft 版本可用的 Fabric Loader。";
    private string _installStatus = "";
    private string? _createdInstanceId;
    private ContentProject? _selectedProject;
    private string _lastDownloadPath = "";
    private string _favoriteStatus = "";
    private bool _favoritesLoadFailed;

    public ObservableCollection<DownloadRowViewModel> Tasks { get; } = [];
    public bool IsEmpty => Tasks.Count == 0;
    public string Summary => $"{Tasks.Count} 项任务 · {Tasks.Count(t => t.Task.State == "Downloading")} 项下载中";
    public DownloadCategoryViewModel MinecraftCategory { get; }
    public IReadOnlyList<DownloadCategoryViewModel> CommunityCategories { get; }
    public IReadOnlyList<DownloadCategoryViewModel> InstallerCategories { get; }
    public DownloadCategoryViewModel TasksCategory { get; }
    public DownloadCategoryViewModel SelectedCategory => _selectedCategory;
    private IEnumerable<DownloadCategoryViewModel> Categories => new[] { MinecraftCategory }.Concat(CommunityCategories).Concat(InstallerCategories).Append(TasksCategory);
    public bool IsCatalogPage => !_isConfiguration && !_isResourceDetail && SelectedCategory.Id == "minecraft";
    public bool IsSearchPage => !_isConfiguration && !_isResourceDetail && SelectedCategory.Id is "mod" or "modpack" or "datapack" or "resourcepack" or "shader" or "world";
    public bool IsInstallerPage => !_isConfiguration && !_isResourceDetail && SelectedCategory.Id.StartsWith("installer-", StringComparison.Ordinal);
    public bool IsFavoritesPage => !_isConfiguration && !_isResourceDetail && SelectedCategory.Id == "favorites";
    public bool IsTasksPage => !_isConfiguration && !_isResourceDetail && SelectedCategory.Id == "tasks";
    public bool IsConfiguration => _isConfiguration;
    public bool IsResourceDetail => _isResourceDetail;
    public bool ShowCategorySidebar => !_isResourceDetail;
    public bool CatalogAvailable => _catalog is not null;
    public bool CanQuery => !_disposed && CatalogAvailable && Owner.CanReadRemote;
    public bool IsLoading => _loading;
    public string RequestStatus { get => _requestStatus; private set => SetProperty(ref _requestStatus, value); }
    public string CatalogSource { get => _catalogSource; private set => SetProperty(ref _catalogSource, value); }
    public bool HasCatalog => LatestVersions.Count > 0 || VersionGroups.Count > 0;
    public ObservableCollection<GameCatalogRowViewModel> LatestVersions { get; } = [];
    public ObservableCollection<GameCatalogGroupViewModel> VersionGroups { get; } = [];
    public string QueryText { get => _queryText; set => SetProperty(ref _queryText, value ?? ""); }
    public string GameVersionFilter { get => _gameVersionFilter; set => SetProperty(ref _gameVersionFilter, value ?? ""); }
    public IReadOnlyList<string> LoaderFilters { get; } = ["任意", "Fabric", "Forge", "NeoForge", "Quilt"];
    public string LoaderFilter { get => _loaderFilter; set => SetProperty(ref _loaderFilter, value ?? "任意"); }
    public IReadOnlyList<ContentSortChoice> SortChoices { get; } = [new("relevance", "默认"), new("downloads", "下载数量"), new("follows", "收藏数量"), new("newest", "最新发布"), new("updated", "最近更新")];
    public ContentSortChoice SelectedSort { get => _selectedSort; set { if (value is not null) SetProperty(ref _selectedSort, value); } }
    public ObservableCollection<ContentProjectRowViewModel> Projects { get; } = [];
    public bool HasProjects => Projects.Count > 0;
    public string SearchPlaceholder => "搜索" + SelectedCategory.Title;
    public bool SearchSupported => SelectedCategory.Id != "world";
    public string SearchCapabilityReason => SelectedCategory.Id == "world"
        ? "当前资源接口尚未提供世界 / 地图检索。" : "当前来源为 Modrinth；其他来源与标签筛选尚未迁移。";
    public string PageSummary => $"第 {_searchOffset / 30 + 1} 页 · {Projects.Count} 项";
    public string QueryCapabilityReason => CatalogAvailable ? "" : "目录服务尚未接入，当前可在实例页手动创建并安装游戏。";
    public string SelectedVersionId => _selectedVersion?.Id ?? "";
    public string InstallationName { get => _installationName; set { if (SetProperty(ref _installationName, value ?? "")) NotifyInstallDraft(); } }
    public string InstallationValidation => string.IsNullOrWhiteSpace(InstallationName) ? "请输入安装名称。"
        : InstallationName.Trim().Length > 80 ? "安装名称最多 80 个字符。"
        : !ValidAdditionalLoader ? AdditionalLoaderValidation
        : Owner.Snapshot.Instances.Any(i => string.Equals(i.Name, InstallationName.Trim(), StringComparison.OrdinalIgnoreCase)) ? "已有同名实例，请修改安装名称。"
        : "名称可用。";
    public bool ValidInstallation => _selectedVersion is { CanInstall: true } && !string.IsNullOrWhiteSpace(InstallationName) && InstallationName.Trim().Length <= 80
        && ValidAdditionalLoader && !Owner.Snapshot.Instances.Any(i => string.Equals(i.Name, InstallationName.Trim(), StringComparison.OrdinalIgnoreCase));
    public string InstallationSummary => _selectedAdditionalLoader is { } loader ? $"Minecraft {SelectedVersionId} + {LoaderTitle(loader.Loader)} {loader.Version}"
        : _selectedFabric is { } fabric ? $"Minecraft {SelectedVersionId} + Fabric Loader {fabric.Version}" : $"Minecraft {SelectedVersionId} · 原版";
    public ObservableCollection<LoaderCatalogVersion> FabricVersions { get; } = [];
    public LoaderCatalogVersion? SelectedFabric
    {
        get => _selectedFabric;
        set
        {
            var previousDefault = DefaultInstallationName;
            if (!SetProperty(ref _selectedFabric, value)) return;
            if (value is not null) ClearAdditionalSelection();
            if (InstallationName == previousDefault) InstallationName = DefaultInstallationName;
            NotifyInstallDraft();
        }
    }
    private string DefaultInstallationName => _selectedAdditionalLoader is { } loader ? $"{SelectedVersionId}-{LoaderTitle(loader.Loader)}_{loader.Version}"
        : _selectedFabric is { } fabric ? $"{SelectedVersionId}-Fabric_{fabric.Version}" : SelectedVersionId;
    public string FabricStatus { get => _fabricStatus; private set => SetProperty(ref _fabricStatus, value); }
    public string InstallStatus { get => _installStatus; private set => SetProperty(ref _installStatus, value); }
    public bool HasCreatedInstance => _createdInstanceId is not null;
    public ContentProject? SelectedProject => _selectedProject;
    public string ProjectTitle => _selectedProject?.Title ?? "";
    public string ProjectDescription => _selectedProject?.Description ?? "";
    public string ProjectDetail => _selectedProject is { } project ? $"{project.Author} · 下载 {project.Downloads:N0} 次 · Modrinth" : "";
    public string ProjectTile => ProjectTitle.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(ProjectTitle);
    public ObservableCollection<ContentReleaseGroupViewModel> ReleaseGroups { get; } = [];
    public bool HasReleases => ReleaseGroups.Count > 0;
    public string LastDownloadPath { get => _lastDownloadPath; private set { if (SetProperty(ref _lastDownloadPath, value)) { OnPropertyChanged(nameof(HasDownloadedFile)); OpenDownloadDirectoryCommand.NotifyCanExecuteChanged(); ReadCachedPackCommand.NotifyCanExecuteChanged(); } } }
    public bool HasDownloadedFile => LastDownloadPath.Length > 0;
    public ObservableCollection<ContentProjectRowViewModel> FavoriteProjects { get; } = [];
    public bool HasFavorites => FavoriteProjects.Count > 0;
    public bool IsProjectFavorite => _selectedProject is { } project && _favorites.Any(p => p.Id == project.Id);
    public string FavoriteAction => IsProjectFavorite ? "取消收藏" : "收藏";
    public string FavoriteStatus { get => _favoriteStatus; private set => SetProperty(ref _favoriteStatus, value); }
    public string InstallerTitle => SelectedCategory.Title;
    private Uri InstallerPageUri => new(SelectedCategory.Id switch
    {
        "installer-optifine" => "https://optifine.net/downloads",
        "installer-forge" => "https://files.minecraftforge.net/net/minecraftforge/forge/",
        "installer-neoforge" => "https://neoforged.net/",
        "installer-cleanroom" => "https://cleanroommc.com/",
        "installer-fabric" => "https://fabricmc.net/use/installer/",
        "installer-legacyfabric" => "https://legacyfabric.net/",
        "installer-labymod" => "https://laby.net/client",
        "installer-liteloader" => "https://www.liteloader.com/",
        _ => "https://www.minecraft.net/"
    });
    public string InstallerDescription => SelectedCategory.Id == "installer-minecraft"
        ? "Minecraft 的自动创建、依赖下载与校验已接入。独立安装包保存尚未迁移，可返回自动安装。"
        : SelectedCategory.Id == "installer-fabric" ? "Fabric Loader 可在 Minecraft 自动安装配置中选择。独立 Fabric 安装器下载尚未迁移。"
        : SelectedCategory.Id is "installer-forge" or "installer-neoforge" ? $"{SelectedCategory.Title}自动安装已在Minecraft配置页接入；独立安装器下载保存尚未迁移。"
        : $"{SelectedCategory.Title} 的安装器下载与安装链尚未迁移。";

    public AsyncRelayCommand RefreshCatalogCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand BackToListCommand { get; }
    public RelayCommand ShowAutomaticInstallCommand { get; }
    public AsyncRelayCommand RefreshFabricCommand { get; }
    public RelayCommand ClearFabricCommand { get; }
    public AsyncRelayCommand InstallVersionCommand { get; }
    public RelayCommand ShowCreatedInstanceCommand { get; }
    public AsyncRelayCommand RefreshReleasesCommand { get; }
    public AsyncRelayCommand OpenProjectPageCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public AsyncRelayCommand OpenDownloadDirectoryCommand { get; }
    public AsyncRelayCommand OpenInstallerPageCommand { get; }

    public DownloadsPageViewModel(MainWindowViewModel owner, ILauncherCatalogService? catalog = null, string? favoritesPath = null,
        IContentInstallationService? contentInstall = null, IModrinthPackService? modrinthPacks = null, ILoaderInstanceService? loaderService = null,
        ILocalPackService? localPacks = null)
        : base(owner, "下载", "Minecraft 自动安装与社区资源")
    {
        _catalog = catalog ?? Service as ILauncherCatalogService;
        _imageToken = _imageLifetime.Token;
        _favoritesPathOverride = favoritesPath;
        InitializeContentInstallation(contentInstall, modrinthPacks, localPacks ?? catalog as ILocalPackService);
        InitializeAdditionalLoaders(loaderService);
        DownloadCategoryViewModel Category(string id, string title) => new(this, id, title, ClubIcons.ForDownloadCategory(id));
        MinecraftCategory = Category("minecraft", "Minecraft");
        CommunityCategories = [Category("mod", "模组"), Category("modpack", "整合包"), Category("datapack", "数据包"), Category("resourcepack", "资源包"), Category("shader", "光影包"), Category("world", "世界"), Category("favorites", "收藏夹")];
        InstallerCategories = [Category("installer-minecraft", "Minecraft"), Category("installer-optifine", "OptiFine"), Category("installer-forge", "Forge"), Category("installer-neoforge", "NeoForge"), Category("installer-cleanroom", "Cleanroom"), Category("installer-fabric", "Fabric"), Category("installer-legacyfabric", "Legacy Fabric"), Category("installer-labymod", "LabyMod"), Category("installer-liteloader", "LiteLoader")];
        TasksCategory = Category("tasks", "下载任务");
        _selectedCategory = MinecraftCategory;
        _selectedSort = SortChoices[0];
        RefreshCatalogCommand = AsyncCommand(RefreshCatalogAsync, () => CanQuery);
        SearchCommand = AsyncCommand(() => SearchAsync(0), () => CanQuery && IsSearchPage && SearchSupported);
        NextPageCommand = AsyncCommand(() => SearchAsync(_searchOffset + 30), () => CanQuery && _searchCompleted && Projects.Count == 30);
        PreviousPageCommand = AsyncCommand(() => SearchAsync(Math.Max(0, _searchOffset - 30)), () => CanQuery && _searchCompleted && _searchOffset > 0);
        ResetFiltersCommand = Command(() => { QueryText = ""; GameVersionFilter = ""; LoaderFilter = "任意"; SelectedSort = SortChoices[0]; }, () => CanEdit);
        BackToListCommand = Command(BackToList);
        ShowAutomaticInstallCommand = Command(() => SelectCategory("minecraft"));
        RefreshFabricCommand = AsyncCommand(RefreshFabricAsync, () => CanQuery && _selectedVersion is not null);
        ClearFabricCommand = Command(ClearAllLoaders, () => CanEdit && (SelectedFabric is not null || SelectedAdditionalLoader is not null));
        InstallVersionCommand = AsyncCommand(InstallVersionAsync, () => CanEdit && ValidInstallation);
        ShowCreatedInstanceCommand = Command(() => Owner.ShowInstancesCommand.Execute(null), () => HasCreatedInstance);
        RefreshReleasesCommand = AsyncCommand(RefreshReleasesAsync, () => CanQuery && _selectedProject is not null);
        OpenProjectPageCommand = AsyncCommand(() => Owner.RunControlAsync("打开项目页", ct => Owner.ExternalLauncher.OpenUriAsync(_selectedProject!.PageUri, ct)), () => _selectedProject is not null);
        ToggleFavoriteCommand = AsyncCommand(ToggleFavoriteAsync, () => CanEdit && !_favoritesLoadFailed && _selectedProject is not null);
        OpenDownloadDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开下载目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(Path.GetDirectoryName(LastDownloadPath)!, ct)), () => HasDownloadedFile);
        OpenInstallerPageCommand = AsyncCommand(() =>
        {
            var uri = InstallerPageUri;
            return Owner.RunControlAsync("打开官方网站", ct => Owner.ExternalLauncher.OpenUriAsync(uri, ct));
        }, () => IsInstallerPage);
        LoadFavorites();
    }

    public void SelectCategory(string id)
    {
        var selected = Categories.FirstOrDefault(c => c.Id == id);
        if (selected is null) return;
        if (!ReferenceEquals(_selectedCategory, selected))
        {
            ++_requestRevision;
            _selectedCategory = selected;
            _isConfiguration = false; _isResourceDetail = false;
            _searchOffset = 0; _searchCompleted = false; ClearProjectRows(Projects);
            RequestStatus = id == "minecraft" ? (_catalogLoaded ? "已读取版本目录。" : "点击刷新读取 Minecraft 版本目录。")
                : id == "world" ? SearchCapabilityReason : "输入条件后点击搜索。";
        }
        NotifyPage();
    }
    private void NotifyPage()
    {
        foreach (var category in Categories) category.RefreshSelection();
        NotifyContentInstallationPage();
        foreach (var name in new[] { nameof(SelectedCategory), nameof(IsCatalogPage), nameof(IsSearchPage), nameof(IsInstallerPage), nameof(IsFavoritesPage), nameof(IsTasksPage), nameof(IsConfiguration), nameof(IsResourceDetail), nameof(ShowCategorySidebar), nameof(SearchPlaceholder), nameof(SearchSupported), nameof(SearchCapabilityReason), nameof(PageSummary), nameof(InstallerTitle), nameof(InstallerDescription), nameof(HasProjects) }) OnPropertyChanged(name);
        NotifyCommands();
    }
    private async Task<bool> QueryAsync(string label, int revision, Func<CancellationToken, Task> action)
    {
        if (!CanQuery) return false;
        _loading = true; OnPropertyChanged(nameof(IsLoading)); RequestStatus = label + "…";
        try
        {
            var success = await Owner.RunAsync(label, async ct =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _imageToken);
                try { await action(lifetime.Token); }
                catch (OperationCanceledException) when (_disposed && !ct.IsCancellationRequested) { }
            }, true);
            if (!success && revision == _requestRevision) RequestStatus = Owner.ErrorMessage ?? "查询已取消，可重试。";
            return success;
        }
        finally { _loading = false; OnPropertyChanged(nameof(IsLoading)); NotifyCommands(); }
    }
    private async Task RefreshCatalogAsync()
    {
        var revision = _requestRevision;
        await QueryAsync("读取 Minecraft 目录", revision, async ct =>
        {
            var catalog = await _catalog!.GetGameCatalogAsync(ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            LatestVersions.Clear(); VersionGroups.Clear();
            var rows = catalog.Versions.OrderByDescending(v => v.ReleaseTime).Select(v => new GameCatalogRowViewModel(this, v)).ToArray();
            foreach (var row in rows.Where(v => v.Version.Latest)) LatestVersions.Add(row);
            foreach (var group in rows.GroupBy(v => v.Version.Kind switch { "release" => "正式版", "snapshot" => "预览版", "old_beta" or "old_alpha" => "远古版", _ => "其他版本" }).OrderBy(g => g.Key switch { "正式版" => 0, "预览版" => 1, "远古版" => 2, _ => 3 }))
                VersionGroups.Add(new(group.Key, group.ToArray()));
            _catalogLoaded = true;
            CatalogSource = "来源：" + catalog.Source + (catalog.Partial ? " · 精简目录，可能缺少历史版本" : "");
            RequestStatus = rows.Length == 0 ? "目录没有返回版本。" : $"已读取 {rows.Length} 个版本。";
            OnPropertyChanged(nameof(HasCatalog)); NotifyCommands();
        });
    }
    public void SelectVersion(GameCatalogVersion version)
    {
        if (!CanEdit || !version.CanInstall) return;
        ++_requestRevision;
        _selectedVersion = version; _selectedFabric = null; _createdInstanceId = null;
        ResetAdditionalLoaders();
        _isConfiguration = true; _isResourceDetail = false; FabricVersions.Clear();
        _installationName = DefaultInstallationName; InstallStatus = "";
        FabricStatus = "点击读取，查询当前 Minecraft 版本的 Fabric Loader。";
        OnPropertyChanged(nameof(InstallationName)); OnPropertyChanged(nameof(SelectedVersionId)); OnPropertyChanged(nameof(SelectedFabric)); OnPropertyChanged(nameof(HasCreatedInstance));
        NotifyInstallDraft(); NotifyPage();
    }
    private void BackToList()
    {
        ++_requestRevision; _isConfiguration = false; _isResourceDetail = false;
        RequestStatus = IsCatalogPage ? "已读取版本目录。" : _searchCompleted ? $"已返回 {Projects.Count} 项资源。" : "输入条件后点击搜索。";
        NotifyPage();
    }
    private async Task RefreshFabricAsync()
    {
        var revision = _requestRevision; var game = SelectedVersionId;
        FabricStatus = "正在读取 Fabric Loader 版本…";
        var success = await QueryAsync("读取 Fabric 目录", revision, async ct =>
        {
            var versions = await _catalog!.GetFabricCatalogAsync(game, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            FabricVersions.Clear(); foreach (var version in versions) FabricVersions.Add(version);
            if (SelectedFabric is not null && !versions.Contains(SelectedFabric)) SelectedFabric = null;
            FabricStatus = versions.Count == 0 ? "此 Minecraft 版本没有可用的 Fabric Loader。" : $"找到 {versions.Count} 个 Fabric Loader 版本，安装前须明确选择。";
        });
        if (!success && revision == _requestRevision) FabricStatus = RequestStatus;
    }
    private void NotifyInstallDraft()
    {
        OnPropertyChanged(nameof(InstallationValidation)); OnPropertyChanged(nameof(ValidInstallation)); OnPropertyChanged(nameof(InstallationSummary));
        InstallVersionCommand?.NotifyCanExecuteChanged(); ClearFabricCommand?.NotifyCanExecuteChanged();
    }
    private async Task InstallVersionAsync()
    {
        if (!ValidInstallation || _selectedVersion is null) return;
        var name = InstallationName.Trim(); var version = _selectedVersion.Id; var fabric = SelectedFabric?.Version; var loader = SelectedAdditionalLoader;
        InstallStatus = "正在创建并安装实例…";
        var success = await Owner.RunAsync("创建并安装游戏", async ct =>
        {
            var instance = loader is not null ? await _loaderService!.CreateLoaderInstanceAsync(name, version, loader.Loader, loader.Version, ct)
                : fabric is null ? await Service.CreateInstanceAsync(name, version, ct)
                : await Service.CreateFabricInstanceAsync(name, version, fabric, ct);
            _createdInstanceId = instance.Id; OnPropertyChanged(nameof(HasCreatedInstance));
            await Service.InstallInstanceAsync(instance.Id, ct);
        });
        InstallStatus = success ? "实例已创建并安装，可返回启动页选择并启动。"
            : _createdInstanceId is null ? Owner.ErrorMessage ?? "创建已取消。" : "实例已创建，但安装尚未完成。可在实例页继续安装。";
        ShowCreatedInstanceCommand.NotifyCanExecuteChanged(); NotifyInstallDraft();
    }
    private async Task SearchAsync(int offset)
    {
        if (!SearchSupported) return;
        if (QueryText.Length > 256) { Owner.ShowError("搜索关键词最多 256 个字符。"); return; }
        var revision = ++_requestRevision;
        var query = new ContentQuery(SelectedCategory.Id, QueryText.Trim(), NullIfBlank(GameVersionFilter), LoaderFilter == "任意" ? null : LoaderFilter.ToLowerInvariant(), SelectedSort.Id, offset);
        await QueryAsync("查询社区资源", revision, async ct =>
        {
            var projects = await _catalog!.SearchContentAsync(query, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            ClearProjectRows(Projects); foreach (var project in projects) Projects.Add(new(this, project));
            _searchOffset = offset; _searchCompleted = true;
            RequestStatus = projects.Count == 0 ? "没有符合条件的资源，请调整搜索条件。" : $"已返回 {projects.Count} 项资源。";
            OnPropertyChanged(nameof(HasProjects)); OnPropertyChanged(nameof(PageSummary)); NotifyCommands();
            using var images = CancellationTokenSource.CreateLinkedTokenSource(ct, _imageToken);
            await Task.WhenAll(Projects.ToArray().Select(p => p.LoadIconAsync(_catalog!, _imageGate, images.Token)));
        });
    }
    public async Task OpenProjectAsync(ContentProject project)
    {
        if (!CanQuery) return;
        ++_requestRevision; _selectedProject = project; _isResourceDetail = true; _isConfiguration = false;
        ResetResourceInstallStatus();
        ReleaseGroups.Clear(); LastDownloadPath = "";
        foreach (var name in new[] { nameof(SelectedProject), nameof(ProjectTitle), nameof(ProjectDescription), nameof(ProjectDetail), nameof(ProjectTile), nameof(IsProjectFavorite), nameof(FavoriteAction), nameof(HasReleases) }) OnPropertyChanged(name);
        NotifyPage(); await RefreshReleasesAsync();
    }
    private async Task RefreshReleasesAsync()
    {
        if (_selectedProject is null) return;
        var projectId = _selectedProject.Id; var revision = _requestRevision;
        var game = NullIfBlank(GameVersionFilter); var loader = LoaderFilter == "任意" ? null : LoaderFilter.ToLowerInvariant();
        await QueryAsync("读取资源文件", revision, async ct =>
        {
            var releases = await _catalog!.GetContentReleasesAsync(projectId, game, loader, ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision) return;
            ReleaseGroups.Clear();
            foreach (var group in releases.OrderByDescending(r => r.Published).GroupBy(r => string.Join(" / ", r.Loaders) + " " + (r.GameVersions.Count > 3 ? $"{r.GameVersions[0]} 等 {r.GameVersions.Count} 个版本" : string.Join(" / ", r.GameVersions))))
                ReleaseGroups.Add(new(group.Key.Trim(), group.Select(r => new ContentReleaseRowViewModel(r, r.Files.OrderByDescending(f => f.Primary).Select(f => new ContentFileRowViewModel(this, f, r)).ToArray())).ToArray()));
            RequestStatus = releases.Count == 0 ? "没有匹配当前版本与加载器的文件。" : $"已读取 {releases.Count} 个资源版本。";
            OnPropertyChanged(nameof(HasReleases)); NotifyCommands();
        });
    }
    internal async Task DownloadFileAsync(ContentFile file)
    {
        if (!CatalogAvailable) return;
        var success = await Owner.RunAsync("下载并校验资源", async ct => LastDownloadPath = await _catalog!.DownloadContentAsync(file, ct));
        RequestStatus = success ? "文件已校验并保存到缓存；尚未自动装入实例或存档。" : Owner.ErrorMessage ?? "下载已取消，可重试。";
    }
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string FavoritesPath => _favoritesPathOverride ?? SHOU.Launcher.Core.IO.SafePath.Within(Service.Paths.DataDirectory, "preferences/download-favorites.json");
    private void LoadFavorites()
    {
        try
        {
            var path = FavoritesPath;
            if (!File.Exists(path)) { FavoriteStatus = "尚未收藏任何资源。"; return; }
            if (new FileInfo(path).Length > 1048576) throw new IOException("收藏文件超过大小限制。");
            var items = System.Text.Json.JsonSerializer.Deserialize<List<ContentProject>>(File.ReadAllText(path)) ?? [];
            if (items.Count > 1000 || items.Any(p => string.IsNullOrWhiteSpace(p.Id) || p.PageUri.Scheme != "https" || p.PageUri.Host != "modrinth.com")) throw new IOException("收藏文件格式无效。");
            _favorites.AddRange(items.DistinctBy(p => p.Id)); RefreshFavorites();
        }
        catch (Exception ex) { _favoritesLoadFailed = true; FavoriteStatus = "收藏读取失败，原文件已保留：" + UiPrivacy.Redact(ex.Message); }
    }
    private void RefreshFavorites()
    {
        ClearProjectRows(FavoriteProjects); foreach (var project in _favorites) FavoriteProjects.Add(new(this, project));
        FavoriteStatus = _favorites.Count == 0 ? "尚未收藏任何资源。" : $"已保存 {_favorites.Count} 项收藏。";
        OnPropertyChanged(nameof(HasFavorites)); OnPropertyChanged(nameof(IsProjectFavorite)); OnPropertyChanged(nameof(FavoriteAction));
    }
    private async Task ToggleFavoriteAsync()
    {
        if (_selectedProject is not { } project) return;
        var next = _favorites.Where(p => p.Id != project.Id).ToList();
        if (!IsProjectFavorite) next.Add(project);
        if (next.Count > 1000) { Owner.ShowError("最多保存 1000 项收藏。"); return; }
        if (await Owner.RunAsync("保存收藏", async ct =>
        {
            var path = FavoritesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_favoritesPathOverride is null) path = FavoritesPath;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, System.Text.Json.JsonSerializer.Serialize(next), ct);
                ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        })) { _favorites.Clear(); _favorites.AddRange(next); RefreshFavorites(); }
        else FavoriteStatus = Owner.ErrorMessage ?? "保存收藏已取消。";
    }
    private static void ClearProjectRows(ObservableCollection<ContentProjectRowViewModel> rows)
    {
        foreach (var row in rows) row.Dispose();
        rows.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_requestRevision; _imageLifetime.Cancel();
        ClearProjectRows(Projects); ClearProjectRows(FavoriteProjects);
        _imageLifetime.Dispose();
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        var ids = snapshot.Downloads.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Tasks.Count - 1; i >= 0; i--) if (!ids.Contains(Tasks[i].Task.Id)) Tasks.RemoveAt(i);
        for (var i = 0; i < snapshot.Downloads.Count; i++)
        {
            var task = snapshot.Downloads[i];
            var row = Tasks.FirstOrDefault(t => t.Task.Id == task.Id);
            if (row is null) Tasks.Insert(i, new(Owner, task));
            else { row.Update(task); if (Tasks.IndexOf(row) != i) Tasks.Move(Tasks.IndexOf(row), i); }
        }
        RefreshContentTargets(snapshot);
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Summary)); NotifyInstallDraft(); base.Refresh(snapshot);
    }
    public override void NotifyCommands()
    {
        foreach (var task in Tasks) { task.CancelCommand.NotifyCanExecuteChanged(); task.RetryCommand.NotifyCanExecuteChanged(); }
        foreach (var row in LatestVersions.Concat(VersionGroups.SelectMany(g => g.Versions))) row.SelectCommand.NotifyCanExecuteChanged();
        foreach (var row in Projects.Concat(FavoriteProjects)) row.OpenCommand.NotifyCanExecuteChanged();
        foreach (var file in ReleaseGroups.SelectMany(g => g.Releases).SelectMany(r => r.Files)) { file.DownloadCommand.NotifyCanExecuteChanged(); file.InstallCommand.NotifyCanExecuteChanged(); }
        ImportLocalPackCommand?.NotifyCanExecuteChanged(); ReadCachedPackCommand?.NotifyCanExecuteChanged();
        foreach (var loader in AdditionalLoaders) loader.NotifyState();
        base.NotifyCommands(); OnPropertyChanged(nameof(CanQuery));
    }
}
