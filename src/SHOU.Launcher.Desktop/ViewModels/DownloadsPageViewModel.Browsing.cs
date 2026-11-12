using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    private sealed class BrowseRead(string label, bool page, CancellationToken token)
    {
        public string Label { get; } = label;
        public bool Page { get; } = page;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
    }
    private sealed record SearchDraft(string Query, string Game, string Loader, ContentSortChoice Sort, string Category, string Source,
        int Offset, bool Completed, DateTimeOffset Fetched, ContentProject[] Projects, string Status, bool ShowStatus);
    private readonly Dictionary<string, BrowseRead> _browseReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SearchDraft> _searchDrafts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _filterDelay;
    private bool _searchRequested, _restoringSearch;
    private DateTimeOffset _searchFetched;
    public RelayCommand CancelBrowseCommand { get; private set; } = null!;
    public bool IsPageLoading => _browseReads.Values.Any(r => r.Page);
    public string PageLoadingText => _browseReads.Values.FirstOrDefault(r => r.Page)?.Label + "…";
    public bool ShowFullPageLoading => IsPageLoading && (IsCatalogPage && !HasCatalog
        || IsResourceDetail && !HasReleases || IsPackageDetail && MinecraftPackages.Count == 0 || IsInstallerPage && !HasInstallerVersions);
    public bool ShowLoadingBadge => IsPageLoading && !ShowFullPageLoading;

    private void InitializeBrowsing() => CancelBrowseCommand = Command(() =>
    {
        CancelBrowseReads(reportCancelled: true);
        SetRequestStatus("查询已取消，可重试。");
        NotifyCommands();
    });

    private void NotifyBrowseState()
    {
        _loading = _browseReads.Count > 0;
        foreach (var name in new[] { nameof(IsLoading), nameof(IsPageLoading), nameof(ShowFullPageLoading), nameof(ShowLoadingBadge), nameof(PageLoadingText) }) OnPropertyChanged(name);
    }
    private void CancelBrowseReads(bool reportCancelled = false)
    {
        _catalogRequestActive = false; _searchRequested = false;
        _filterDelay?.Cancel(); _filterDelay = null;
        foreach (var read in _browseReads.Values.ToArray()) read.Cancellation.Cancel();
        foreach (var loader in AdditionalLoaders.Where(x => x.IsLoading))
        { loader.IsLoading = false; if (reportCancelled) loader.Status = "已取消，可重试。"; }
        if (FabricLoading && reportCancelled) FabricStatus = "已取消，可重试。";
        FabricLoading = false;
        _browseReads.Clear(); NotifyBrowseState();
    }
    public void OnNavigatingAway()
    {
        SaveSearchDraft(); ++_requestRevision; CancelBrowseReads();
        _catalogRequestActive = false;
        foreach (var loader in AdditionalLoaders) loader.IsLoading = false;
        FabricLoading = false;
    }
    private async Task<bool> ReadBrowseAsync(string label, int revision, Func<CancellationToken, Task> action,
        string key = "page", bool page = true, Action<string>? failure = null)
    {
        if (!CanQuery) return false;
        if (_browseReads.TryGetValue(key, out var previous)) previous.Cancellation.Cancel();
        using var read = new DisposableRead(new(label, page, _imageToken));
        var request = read.Value;
        request.Cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        _browseReads[key] = request;
        if (page) SetRequestStatus("", false);
        NotifyBrowseState(); NotifyCommands();
        bool Current() => !_disposed && revision == _requestRevision && _browseReads.GetValueOrDefault(key) == request;
        try { await action(request.Cancellation.Token); return Current() && !request.Cancellation.IsCancellationRequested; }
        catch (Exception ex)
        {
            if (Current())
            {
                var message = ex is OperationCanceledException ? "读取超时，请重试。" : UiPrivacy.Redact(ex.Message);
                if (failure is not null) failure(message); else SetRequestStatus(message);
            }
            return false;
        }
        finally
        {
            if (_browseReads.GetValueOrDefault(key) == request) _browseReads.Remove(key);
            NotifyBrowseState(); NotifyCommands();
        }
    }
    private sealed class DisposableRead(BrowseRead value) : IDisposable
    { public BrowseRead Value { get; } = value; public void Dispose() => Value.Cancellation.Dispose(); }

    private void SaveSearchDraft()
    {
        if (!IsSearchPage && !IsResourceDetail) return;
        _searchDrafts[SelectedCategory.Id] = new(QueryText, GameVersionFilter, LoaderFilter, SelectedSort, SelectedContentCategory.Id, SelectedSource.Id, _searchOffset,
            _searchCompleted, _searchFetched, Projects.Select(p => p.Project).ToArray(), RequestStatus, ShowRequestStatus);
    }
    private void RestoreSearchDraft(string category)
    {
        _restoringSearch = true;
        try
        {
            _searchDrafts.TryGetValue(category, out var draft);
            QueryText = draft?.Query ?? ""; GameVersionFilter = draft?.Game ?? ""; LoaderFilter = draft?.Loader ?? "任意";
            SelectedSource = ContentSourceChoices.FirstOrDefault(s => s.Id == draft?.Source) ?? ContentSourceChoices[0];
            SelectedSort = draft?.Sort ?? SortChoices[0];
            _contentCategory = new(draft?.Category ?? "", CategoryName(draft?.Category ?? "")); UpdateContentCategories();
            _searchOffset = draft?.Offset ?? 0; _searchCompleted = draft?.Completed ?? false; _searchFetched = draft?.Fetched ?? DateTimeOffset.MinValue;
            ClearProjectRows(Projects);
            foreach (var project in draft?.Projects ?? []) Projects.Add(new(this, project));
            SetRequestStatus(draft?.Status ?? "", draft?.ShowStatus ?? false);
        }
        finally { _restoringSearch = false; }
    }
    private void FiltersChanged()
    {
        if (_restoringSearch || _disposed || (!IsSearchPage && !IsResourceDetail)) return;
        _filterDelay?.Cancel();
        var delay = CancellationTokenSource.CreateLinkedTokenSource(_imageToken); _filterDelay = delay;
        _ = RefreshFiltersAfterDelayAsync(delay);
    }
    private async Task RefreshFiltersAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(350, delay.Token);
            if (!ReferenceEquals(Owner.SelectedPage, this) || _disposed) return;
            if (IsResourceDetail) await RefreshReleasesAsync();
            else { _searchRequested = true; TryStartSearchRequest(); }
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally { if (_filterDelay == delay) _filterDelay = null; delay.Dispose(); }
    }
    private void TryStartSearchRequest()
    {
        if (!_searchRequested || !CanQuery || !IsSearchPage || !SearchSupported || !ReferenceEquals(Owner.SelectedPage, this)) return;
        _searchRequested = false; _ = SearchAsync(0);
    }
    private void RestoreSearchIcons()
    {
        if (_catalog is null || !CanQuery || !IsSearchPage || _browseReads.ContainsKey("cached-icons") || Projects.All(p => p.HasIcon || string.IsNullOrWhiteSpace(p.Project.IconUrl))) return;
        var rows = Projects.ToArray(); var revision = _requestRevision;
        _ = ReadBrowseAsync("读取资源图标", revision, ct => Task.WhenAll(rows.Select(p => p.LoadIconAsync(_catalog, _imageGate, ct))),
            "cached-icons", page: false, failure: _ => { });
    }
}
