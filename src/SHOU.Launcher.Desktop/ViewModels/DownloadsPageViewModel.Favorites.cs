using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record FavoriteKindGroup(string Title, IReadOnlyList<ContentProjectRowViewModel> Projects) { public int Count => Projects.Count; }
public sealed partial class DownloadsPageViewModel
{
    private string FavoritesPath => _favoritesPathOverride ?? SafePath.Within(Service.Paths.DataDirectory, "preferences/download-favorites.json");
    public ObservableCollection<FavoriteFolder> FavoriteFolders { get; } = [];
    private FavoriteFolder? favoriteFolder, favoriteDestination;
    private string favoriteQuery = "", favoriteFolderName = "", favoriteShareCode = "", deletingFavoriteFolder = "";
    public FavoriteFolder? SelectedFavoriteFolder { get => favoriteFolder; set { if (SetProperty(ref favoriteFolder, value)) { foreach (var row in FavoriteProjects) { row.IsSelected = false; row.SetFavoriteNote(value?.Notes?.GetValueOrDefault(row.Project.Id) ?? ""); } deletingFavoriteFolder = ""; FavoriteFolderName = value?.Name ?? ""; FavoriteDestinationFolder = FavoriteFolders.FirstOrDefault(f => f.Id != value?.Id); NotifyFavoriteFilter(); OnPropertyChanged(nameof(IsProjectFavorite)); OnPropertyChanged(nameof(FavoriteAction)); } } }
    public FavoriteFolder? FavoriteDestinationFolder { get => favoriteDestination; set => SetProperty(ref favoriteDestination, value); }
    public string FavoriteFolderName { get => favoriteFolderName; set => SetProperty(ref favoriteFolderName, value ?? ""); }
    public string FavoriteShareCode { get => favoriteShareCode; set => SetProperty(ref favoriteShareCode, value ?? ""); }
    public string FavoriteQuery { get => favoriteQuery; set { if (SetProperty(ref favoriteQuery, value ?? "")) NotifyFavoriteFilter(); } }
    private IEnumerable<ContentProjectRowViewModel> CurrentFavoriteRows => FavoriteProjects.Where(p => SelectedFavoriteFolder?.Projects.Contains(p.Project.Id) ?? true);
    public IReadOnlyList<ContentProjectRowViewModel> FilteredFavoriteProjects => CurrentFavoriteRows.Select(p => (Row: p, Score: FavoriteData.SearchScore(p.Project, p.FavoriteNote, FavoriteQuery))).Where(p => p.Score > 0).OrderByDescending(p => p.Score).Select(p => p.Row).ToArray();
    public IReadOnlyList<FavoriteKindGroup> FavoriteGroups => FilteredFavoriteProjects.GroupBy(r => FavoriteQuery.Trim().Length > 0 ? "search" : r.Project.Kind).Select(g => new FavoriteKindGroup((g.Key switch { "mod" => "模组", "modpack" => "整合包", "resourcepack" => "资源包", "shader" => "光影包", "datapack" => "数据包", "world" => "世界", "search" => "搜索结果", _ => "其他资源" }), g.ToArray())).ToArray();
    public string[] UnresolvedFavorites => SelectedFavoriteFolder?.Unresolved ?? [];
    public bool HasUnresolvedFavorites => UnresolvedFavorites.Length > 0;
    public string UnresolvedFavoriteSummary => Localization.UiLocalization.Instance.Format("暂未读取的收藏：{0}", string.Join(", ", UnresolvedFavorites));
    public bool IsFavoriteFolderEmpty => !CurrentFavoriteRows.Any() && !HasUnresolvedFavorites;
    public bool HasNoFavoriteMatches => !IsFavoriteFolderEmpty && FavoriteQuery.Trim().Length > 0 && FilteredFavoriteProjects.Count == 0;
    public bool HasFavoriteSelection => CurrentFavoriteRows.Any(r => r.IsSelected);
    public bool HasFavoriteFolderDeletion => deletingFavoriteFolder.Length > 0;
    public string FavoriteFolderDeletionSummary => Localization.UiLocalization.Instance.Format("删除收藏夹“{0}”及其收藏记录？其他收藏夹不受影响。", FavoriteFolders.FirstOrDefault(f => f.Id == deletingFavoriteFolder)?.Name);
    public AsyncRelayCommand CreateFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RenameFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand DeleteFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmDeleteFavoriteFolderCommand { get; private set; } = null!;
    public RelayCommand CancelDeleteFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveSelectedFavoritesCommand { get; private set; } = null!;
    public AsyncRelayCommand CopySelectedFavoritesCommand { get; private set; } = null!;
    public AsyncRelayCommand MoveSelectedFavoritesCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportFavoriteShareCommand { get; private set; } = null!;
    public AsyncRelayCommand RetryUnresolvedFavoritesCommand { get; private set; } = null!;
    public RelayCommand SelectAllFavoritesCommand { get; private set; } = null!;
    private void InitializeFavorites()
    {
        bool Editable() => CanEdit && !_favoritesLoadFailed;
        CreateFavoriteFolderCommand = new(() => SaveFavoritesAsync(_favorites.ToArray(), FavoriteFolders.Append(new(Guid.NewGuid().ToString("N"), FolderName(), [])).ToArray(), selectLast: true), Editable);
        RenameFavoriteFolderCommand = new(() => SaveFavoritesAsync(_favorites.ToArray(), FavoriteFolders.Select(f => f.Id == SelectedFavoriteFolder?.Id ? f with { Name = FolderName() } : f).ToArray()), Editable);
        DeleteFavoriteFolderCommand = new(() => { deletingFavoriteFolder = SelectedFavoriteFolder?.Id ?? ""; NotifyFavoriteFilter(); return Task.CompletedTask; }, () => Editable() && FavoriteFolders.Count > 1);
        ConfirmDeleteFavoriteFolderCommand = new(DeleteFavoriteFolderAsync, Editable);
        CancelDeleteFavoriteFolderCommand = new(() => { deletingFavoriteFolder = ""; NotifyFavoriteFilter(); });
        RemoveSelectedFavoritesCommand = new(RemoveSelectedFavoritesAsync, Editable);
        CopySelectedFavoritesCommand = new(() => TransferFavoriteSelectionAsync(false), Editable); MoveSelectedFavoritesCommand = new(() => TransferFavoriteSelectionAsync(true), Editable);
        ImportFavoriteShareCommand = new(() => ImportLegacyFavoritesAsync(FavoriteShareCode, false), Editable);
        RetryUnresolvedFavoritesCommand = new(() => ImportLegacyFavoritesAsync(JsonSerializer.Serialize(UnresolvedFavorites), false), Editable);
        SelectAllFavoritesCommand = new(() => { var rows = FilteredFavoriteProjects; var select = rows.Any(r => !r.IsSelected); foreach (var row in rows) row.IsSelected = select; });
        InitializeFavoriteDownloads();
    }
    private string FolderName() => string.IsNullOrWhiteSpace(FavoriteFolderName) ? Localization.UiLocalization.Instance.Text("新收藏夹") : FavoriteFolderName.Trim();
    private void NotifyFavoriteFilter()
    { foreach (var name in new[] { nameof(FilteredFavoriteProjects), nameof(FavoriteGroups), nameof(HasNoFavoriteMatches), nameof(IsFavoriteFolderEmpty), nameof(UnresolvedFavorites), nameof(HasUnresolvedFavorites), nameof(UnresolvedFavoriteSummary), nameof(HasFavoriteSelection), nameof(HasFavoriteFolderDeletion), nameof(FavoriteFolderDeletionSummary) }) OnPropertyChanged(name); }
    internal void FavoriteSelectionChanged() { OnPropertyChanged(nameof(HasFavoriteSelection)); InvalidateFavoriteDownload(); }
    private void LoadFavorites()
    {
        try
        {
            var path = FavoritesPath; SafePath.RejectLinks(path);
            if (!File.Exists(path)) { FavoriteFolders.Add(new("default", Localization.UiLocalization.Instance.Text("默认收藏夹"), [])); SelectedFavoriteFolder = FavoriteFolders[0]; FavoriteStatus = "尚未收藏任何资源。"; return; }
            if (new FileInfo(path).Length > 1048576) throw new IOException("收藏文件超过大小限制。");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var document = json.RootElement.ValueKind == JsonValueKind.Array ? new FavoriteDocument(1, json.RootElement.Deserialize<ContentProject[]>() ?? [], []) : json.RootElement.Deserialize<FavoriteDocument>() ?? throw new IOException("收藏文件格式无效。");
            var items = document.Projects; ValidateFavoriteProjects(items);
            if (document.Version is not (1 or 2 or 3) || document.Folders is null || document.Folders.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Name) || f.Projects is null || f.Projects.Any(id => !items.Any(p => p.Id == id))) || document.Folders.Select(f => f.Id).Distinct().Count() != document.Folders.Length) throw new IOException("收藏分组格式无效。");
            foreach (var folder in document.Folders) { foreach (var id in folder.Unresolved ?? []) _ = FavoriteData.CanonicalId(id); if (folder.Notes?.Any(n => n.Value is null || !folder.Projects.Concat(folder.Unresolved ?? []).Contains(n.Key)) == true) throw new IOException("收藏备注格式无效。"); }
            _favorites.AddRange(items.DistinctBy(p => p.Id)); foreach (var folder in document.Folders) FavoriteFolders.Add(folder);
            if (FavoriteFolders.Count == 0) FavoriteFolders.Add(new("default", Localization.UiLocalization.Instance.Text("默认收藏夹"), items.Select(p => p.Id).ToArray()));
            SelectedFavoriteFolder = FavoriteFolders[0]; RefreshFavorites();
        }
        catch (Exception ex) { _favoritesLoadFailed = true; FavoriteStatus = "收藏读取失败，原文件已保留：" + UiPrivacy.Redact(ex.Message); }
    }
    private static void ValidateFavoriteProjects(ContentProject[] items)
    {
        if (items is null || items.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.PageUri is null || !p.PageUri.IsAbsoluteUri || p.PageUri.Scheme != "https" || !p.PageUri.IsDefaultPort || p.PageUri.UserInfo.Length != 0 || (p.Source == "curseforge" ? !p.Id.StartsWith("cf-", StringComparison.Ordinal) || p.PageUri.Host is not ("www.curseforge.com" or "curseforge.com") : p.Source != "modrinth" || p.PageUri.Host != "modrinth.com"))) throw new IOException("收藏文件格式无效。");
    }
    private void RefreshFavorites()
    {
        ClearProjectRows(FavoriteProjects); foreach (var project in _favorites) { var row = new ContentProjectRowViewModel(this, project); row.SetFavoriteNote(SelectedFavoriteFolder?.Notes?.GetValueOrDefault(project.Id) ?? ""); FavoriteProjects.Add(row); }
        FavoriteStatus = _favorites.Count == 0 ? "尚未收藏任何资源。" : $"已保存 {_favorites.Count} 项收藏。";
        foreach (var property in new[] { nameof(HasFavorites), nameof(IsProjectFavorite), nameof(FavoriteAction) }) OnPropertyChanged(property);
        NotifyFavoriteFilter(); RestoreFavoriteIcons(); InvalidateFavoriteDownload();
    }
    private async Task<bool> SaveFavoritesAsync(ContentProject[] projects, FavoriteFolder[] folders, bool selectLast = false)
    {
        try { ValidateFavoriteProjects(projects); } catch (IOException ex) { Owner.ShowError(ex.Message); return false; }
        var selected = selectLast ? folders.Last().Id : SelectedFavoriteFolder?.Id;
        if (!await Owner.RunAsync("保存收藏", async ct =>
        {
            var path = FavoritesPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); SafePath.RejectLinks(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { var json = JsonSerializer.SerializeToUtf8Bytes(new FavoriteDocument(3, projects, folders)); if (json.Length > 1048576) throw new IOException("收藏文件超过大小限制。"); await File.WriteAllBytesAsync(temporary, json, ct); ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(path); SafePath.RejectLinks(path + ".bak"); if (File.Exists(path)) File.Copy(path, path + ".bak", true); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        })) { FavoriteStatus = Owner.ErrorMessage ?? "保存收藏已取消。"; return false; }
        _favorites.Clear(); _favorites.AddRange(projects); FavoriteFolders.Clear(); foreach (var f in folders) FavoriteFolders.Add(f); SelectedFavoriteFolder = FavoriteFolders.FirstOrDefault(f => f.Id == selected) ?? FavoriteFolders.FirstOrDefault(); RefreshFavorites(); NotifyCommands(); return true;
    }
    private static FavoriteFolder RemoveIds(FavoriteFolder folder, HashSet<string> ids) => folder with { Projects = folder.Projects.Where(id => !ids.Contains(id)).ToArray(), Unresolved = (folder.Unresolved ?? []).Where(id => !ids.Contains(id)).ToArray(), Notes = (folder.Notes ?? []).Where(n => !ids.Contains(n.Key)).ToDictionary() };
    private async Task ToggleFavoriteAsync()
    {
        if (_selectedProject is not { } project) return; var remove = IsProjectFavorite; var target = SelectedFavoriteFolder?.Id;
        var folders = FavoriteFolders.Select(f => f.Id != target ? f : remove ? RemoveIds(f, [project.Id]) : f with { Projects = f.Projects.Append(project.Id).Distinct().ToArray() }).ToArray();
        var remaining = folders.SelectMany(f => f.Projects).ToHashSet(); await SaveFavoritesAsync(_favorites.Append(project).DistinctBy(p => p.Id).Where(p => remaining.Contains(p.Id)).ToArray(), folders);
    }
    private async Task DeleteFavoriteFolderAsync()
    {
        if (FavoriteFolders.Count <= 1 || !FavoriteFolders.Any(f => f.Id == deletingFavoriteFolder)) return; var folders = FavoriteFolders.Where(f => f.Id != deletingFavoriteFolder).ToArray(); var remaining = folders.SelectMany(f => f.Projects).ToHashSet();
        if (await SaveFavoritesAsync(_favorites.Where(p => remaining.Contains(p.Id)).ToArray(), folders)) { deletingFavoriteFolder = ""; NotifyFavoriteFilter(); }
    }
    private Task RemoveSelectedFavoritesAsync() => RemoveFavoriteIdsAsync(FilteredFavoriteProjects.Where(r => r.IsSelected).Select(r => r.Project.Id).ToHashSet());
    internal Task RemoveFavoriteRowAsync(ContentProjectRowViewModel row) => CurrentFavoriteRows.Contains(row) ? RemoveFavoriteIdsAsync([row.Project.Id]) : Task.CompletedTask;
    private Task RemoveFavoriteIdsAsync(HashSet<string> ids)
    { var folders = FavoriteFolders.Select(f => f.Id == SelectedFavoriteFolder?.Id ? RemoveIds(f, ids) : f).ToArray(); var remaining = folders.SelectMany(f => f.Projects).ToHashSet(); return ids.Count == 0 ? Task.CompletedTask : SaveFavoritesAsync(_favorites.Where(p => remaining.Contains(p.Id)).ToArray(), folders); }
    internal async Task<bool> SaveFavoriteNoteAsync(ContentProjectRowViewModel row, string note)
    {
        if (!CurrentFavoriteRows.Contains(row)) return false;
        var folders = FavoriteFolders.Select(f => { if (f.Id != SelectedFavoriteFolder?.Id) return f; var notes = new Dictionary<string, string>(f.Notes ?? []); if (string.IsNullOrWhiteSpace(note)) notes.Remove(row.Project.Id); else notes[row.Project.Id] = note.Trim(); return f with { Notes = notes }; }).ToArray();
        return await SaveFavoritesAsync(_favorites.ToArray(), folders);
    }
    private async Task TransferFavoriteSelectionAsync(bool move)
    {
        if (SelectedFavoriteFolder is not { } source || FavoriteDestinationFolder is not { } destination || source.Id == destination.Id || !FavoriteFolders.Contains(destination)) return;
        var ids = CurrentFavoriteRows.Where(r => r.IsSelected).Select(r => r.Project.Id).ToHashSet(); if (ids.Count == 0) return;
        var folders = FavoriteFolders.Select(f => { if (f.Id == destination.Id) { var notes = new Dictionary<string, string>(f.Notes ?? []); foreach (var id in ids) if (!notes.ContainsKey(id) && source.Notes?.TryGetValue(id, out var note) == true) notes[id] = note; return f with { Projects = f.Projects.Concat(ids).Distinct().ToArray(), Notes = notes }; } return move && f.Id == source.Id ? RemoveIds(f, ids) : f; }).ToArray();
        await SaveFavoritesAsync(_favorites.ToArray(), folders);
    }
    public string GetFavoriteShareCode()
    { var rows = CurrentFavoriteRows.ToArray(); var chosen = rows.Any(r => r.IsSelected) ? rows.Where(r => r.IsSelected) : rows; return JsonSerializer.Serialize(chosen.Select(r => r.Project.Source == "curseforge" ? r.Project.Id[3..] : r.Project.Id).Concat(rows.Any(r => r.IsSelected) ? [] : UnresolvedFavorites.Select(id => id.StartsWith("cf-", StringComparison.Ordinal) ? id[3..] : id)).Distinct()); }
    public async Task ImportLegacyFavoritesAsync(string text, bool preserveFolders = true)
    {
        if (_catalog is not IContentDependencyCatalogService catalog) { FavoriteStatus = "当前资源服务不支持读取分享项目。"; return; }
        IReadOnlyList<LegacyFavoriteFolder> imported;
        try { imported = FavoriteData.ReadLegacy(text, FolderName()); } catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or ArgumentException) { Owner.ShowError(UiPrivacy.Redact(ex.Message)); return; }
        var target = SelectedFavoriteFolder?.Id; var additions = new Dictionary<string, ContentProject>(); var failures = new HashSet<string>();
        if (!await Owner.RunAsync("导入收藏", async ct =>
        {
            foreach (var id in imported.SelectMany(f => f.Projects).Distinct())
            { ct.ThrowIfCancellationRequested(); if (_favorites.FirstOrDefault(p => p.Id == id) is { } saved) { additions[id] = saved; continue; }
              try { var project = await catalog.GetContentProjectAsync(id, ct); if (project.Id != id) throw new InvalidDataException("资源身份不符。"); ValidateFavoriteProjects([project]); additions[id] = project; }
              catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or ArgumentException or InvalidOperationException) { failures.Add(id); } }
        })) return;
        var folders = FavoriteFolders.ToList();
        foreach (var folder in imported)
        {
            var id = preserveFolders ? "wpf-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder.Id.Length > 0 ? folder.Id : folder.Name)))[..24] : target!;
            var index = folders.FindIndex(f => f.Id == id); var previous = index < 0 ? new FavoriteFolder(id, folder.Name, []) : folders[index];
            var notes = new Dictionary<string, string>(previous.Notes ?? []); foreach (var note in folder.Notes) notes.TryAdd(note.Key, note.Value);
            var updated = previous with { Projects = previous.Projects.Concat(folder.Projects.Where(additions.ContainsKey)).Distinct().ToArray(), Unresolved = (previous.Unresolved ?? []).Concat(folder.Projects.Where(failures.Contains)).Where(i => !additions.ContainsKey(i)).Distinct().ToArray(), Notes = notes };
            if (index < 0) folders.Add(updated); else folders[index] = updated;
        }
        if (await SaveFavoritesAsync(_favorites.Concat(additions.Values).DistinctBy(p => p.Id).ToArray(), folders.ToArray())) FavoriteStatus = Localization.UiLocalization.Instance.Format("已导入 {0} 项收藏。", additions.Count) + (failures.Count > 0 ? " " + Localization.UiLocalization.Instance.Format("无法读取（已保留，可重试）：{0}", string.Join(", ", failures)) : "");
    }
}
