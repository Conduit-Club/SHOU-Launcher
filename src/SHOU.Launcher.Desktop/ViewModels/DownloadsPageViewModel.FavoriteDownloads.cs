using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    private ContentProject[] favoriteDownloadProjects = [];
    private Dictionary<string, IReadOnlyList<ContentRelease>> favoriteDownloadReleases = [];
    private string favoriteBatchLoader = "", favoriteBatchVersion = "", favoriteBatchStatus = "";
    private IReadOnlyList<string> favoriteBatchLoaders = [], favoriteBatchVersions = [];
    private int favoriteBatchRevision;
    private CancellationTokenSource? favoriteBatchRead;
    public string FavoriteBatchIdentity => favoriteBatchRevision + "|" + FavoriteBatchLoader + "|" + FavoriteBatchVersion;
    public string FavoriteBatchSummary { get { try { return CanSaveFavoriteBatch ? string.Join("\n", FavoriteDownloads.Plan(favoriteDownloadProjects, favoriteDownloadReleases, FavoriteBatchLoader, FavoriteBatchVersion).Select(e => e.Title + " → " + e.DestinationName)) : ""; } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return UiPrivacy.Redact(ex.Message); } } }
    public IReadOnlyList<string> FavoriteBatchLoaders => favoriteBatchLoaders;
    public IReadOnlyList<string> FavoriteBatchVersions => favoriteBatchVersions;
    public string FavoriteBatchLoader { get => favoriteBatchLoader; set { if (SetProperty(ref favoriteBatchLoader, value ?? "")) { FavoriteBatchVersion = ""; favoriteBatchVersions = FavoriteDownloads.CommonVersions(favoriteDownloadProjects, favoriteDownloadReleases, FavoriteBatchLoader); OnPropertyChanged(nameof(FavoriteBatchVersions)); FavoriteBatchVersion = FavoriteBatchVersions.FirstOrDefault() ?? ""; } } }
    public string FavoriteBatchVersion { get => favoriteBatchVersion; set { if (SetProperty(ref favoriteBatchVersion, value ?? "")) { OnPropertyChanged(nameof(CanSaveFavoriteBatch)); OnPropertyChanged(nameof(FavoriteBatchSummary)); } } }
    public string FavoriteBatchStatus { get => favoriteBatchStatus; private set => SetProperty(ref favoriteBatchStatus, value); }
    public bool HasFavoriteBatch => favoriteDownloadProjects.Length > 0;
    public bool CanSaveFavoriteBatch => HasFavoriteBatch && FavoriteBatchVersions.Contains(FavoriteBatchVersion) && FavoriteBatchLoaders.Contains(FavoriteBatchLoader) && CanEdit;
    public AsyncRelayCommand PrepareFavoriteDownloadCommand { get; private set; } = null!;
    public RelayCommand CancelFavoriteDownloadCommand { get; private set; } = null!;
    private void InitializeFavoriteDownloads()
    {
        PrepareFavoriteDownloadCommand = new(PrepareFavoriteDownloadAsync, () => CanQuery && HasFavoriteSelection);
        CancelFavoriteDownloadCommand = new(InvalidateFavoriteDownload);
    }
    private void InvalidateFavoriteDownload()
    {
        ++favoriteBatchRevision; favoriteBatchRead?.Cancel(); favoriteDownloadProjects = []; favoriteDownloadReleases.Clear(); favoriteBatchLoaders = []; favoriteBatchVersions = []; FavoriteBatchLoader = ""; FavoriteBatchVersion = "";
        foreach (var name in new[] { nameof(HasFavoriteBatch), nameof(CanSaveFavoriteBatch), nameof(FavoriteBatchLoaders), nameof(FavoriteBatchVersions), nameof(FavoriteBatchSummary) }) OnPropertyChanged(name);
        PrepareFavoriteDownloadCommand?.NotifyCanExecuteChanged();
    }
    private async Task PrepareFavoriteDownloadAsync()
    {
        var projects = CurrentFavoriteRows.Where(r => r.IsSelected).Select(r => r.Project).ToArray(); if (projects.Length == 0 || _catalog is null) return;
        InvalidateFavoriteDownload();
        var revision = favoriteBatchRevision; var releases = new Dictionary<string, IReadOnlyList<ContentRelease>>(); var failed = new List<string>();
        FavoriteBatchStatus = "";
        var success = await Owner.RunAsync("读取收藏共同版本", async ct =>
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct); favoriteBatchRead = read; ct = read.Token;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(projects.Select(async project =>
            {
                await gate.WaitAsync(ct);
                try { var versions = await _catalog.GetContentReleasesAsync(project.Id, cancellationToken: ct); lock (releases) releases[project.Id] = versions; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException) { lock (failed) failed.Add(project.Title); }
                finally { gate.Release(); }
            }));
        });
        favoriteBatchRead = null;
        if (!success || revision != favoriteBatchRevision) return;
        if (failed.Count > 0) { FavoriteBatchStatus = Localization.UiLocalization.Instance.Format("未能读取所选项目，未下载：{0}", string.Join(", ", failed)); FavoriteStatus = FavoriteBatchStatus; return; }
        favoriteDownloadProjects = projects; favoriteDownloadReleases = releases;
        // Publish stable choices before selecting; replacing ItemsSource after selection clears Avalonia's selection.
        favoriteBatchLoaders = FavoriteDownloads.CommonLoaders(projects, releases); OnPropertyChanged(nameof(FavoriteBatchLoaders)); FavoriteBatchLoader = FavoriteBatchLoaders.FirstOrDefault() ?? "";
        FavoriteBatchStatus = FavoriteBatchVersion.Length == 0 ? "所选收藏没有共同支持的加载器或游戏版本。" : "选择共同版本和保存目录后下载；仅保存文件，不安装到实例。所选版本可能包含测试版。";
        foreach (var name in new[] { nameof(HasFavoriteBatch), nameof(CanSaveFavoriteBatch), nameof(FavoriteBatchSummary) }) OnPropertyChanged(name);
    }
    public async Task SaveFavoriteDownloadAsync(string directory, string? expectedIdentity = null)
    {
        if (!CanSaveFavoriteBatch || _catalog is null || expectedIdentity is not null && expectedIdentity != FavoriteBatchIdentity) return;
        var plan = FavoriteDownloads.Plan(favoriteDownloadProjects, favoriteDownloadReleases, FavoriteBatchLoader, FavoriteBatchVersion);
        FavoriteDownloadResult? result = null; FavoriteBatchStatus = "";
        Owner.ShowTaskNotification(this, "收藏批量下载", "已开始下载；点击查看下载过程。");
        if (!await Owner.RunBackgroundInstallAsync("批量下载收藏", async ct => result = Service is IContentBatchDownloadService batch ? await batch.DownloadFavoriteFilesAsync(plan, directory, ct) : await FavoriteDownloads.ExportAsync(plan, directory, _catalog.DownloadContentAsync, ct), ex => FavoriteBatchStatus = UiPrivacy.Redact(ex.Message)))
        { if (FavoriteBatchStatus.Length == 0) FavoriteBatchStatus = Owner.ErrorMessage ?? "下载已取消或失败；已完成的文件保留。"; Owner.ShowTaskNotification(this, "收藏批量下载", FavoriteBatchStatus); return; }
        FavoriteBatchStatus = Localization.UiLocalization.Instance.Format("已保存 {0} 个文件，复用 {1} 个文件。", result!.Added, result.Reused) + (result.Failures.Count > 0 ? " " + Localization.UiLocalization.Instance.Format("未完成：{0}", string.Join("；", result.Failures.Select(f => f.Name + "：" + UiPrivacy.Redact(f.Reason)))) : "");
        Owner.ShowTaskNotification(this, "收藏批量下载", FavoriteBatchStatus);
    }
}
