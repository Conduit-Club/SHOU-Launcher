using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record FavoriteFolder(string Id, string Name, string[] Projects);
public sealed record FavoriteDocument(int Version, ContentProject[] Projects, FavoriteFolder[] Folders);
public sealed partial class DownloadsPageViewModel
{
    private string FavoritesPath => _favoritesPathOverride ?? SafePath.Within(Service.Paths.DataDirectory, "preferences/download-favorites.json");
    public ObservableCollection<FavoriteFolder> FavoriteFolders { get; } = [];
    private FavoriteFolder? favoriteFolder;
    private string favoriteQuery = "", favoriteFolderName = "", favoriteShareCode = "";
    public FavoriteFolder? SelectedFavoriteFolder { get => favoriteFolder; set { if (SetProperty(ref favoriteFolder, value)) { FavoriteFolderName = value?.Name ?? ""; NotifyFavoriteFilter(); OnPropertyChanged(nameof(IsProjectFavorite)); OnPropertyChanged(nameof(FavoriteAction)); } } }
    public string FavoriteFolderName { get => favoriteFolderName; set => SetProperty(ref favoriteFolderName, value ?? ""); }
    public string FavoriteShareCode { get => favoriteShareCode; set => SetProperty(ref favoriteShareCode, value ?? ""); }
    public string FavoriteQuery { get => favoriteQuery; set { if (SetProperty(ref favoriteQuery, value ?? "")) NotifyFavoriteFilter(); } }
    public IReadOnlyList<ContentProjectRowViewModel> FilteredFavoriteProjects => FavoriteProjects.Where(p => (SelectedFavoriteFolder?.Projects.Contains(p.Project.Id) ?? true) && string.Join(" ", p.Project.Title, p.Project.Slug, p.Project.Author, p.Project.Description, p.Project.Kind, p.Project.Source).Contains(FavoriteQuery.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasNoFavoriteMatches => FilteredFavoriteProjects.Count == 0;
    public AsyncRelayCommand CreateFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RenameFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand DeleteFavoriteFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveSelectedFavoritesCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportFavoriteShareCommand { get; private set; } = null!;
    public RelayCommand SelectAllFavoritesCommand { get; private set; } = null!;
    private void InitializeFavorites()
    {
        bool Editable() => CanEdit && !_favoritesLoadFailed;
        CreateFavoriteFolderCommand = new(() => SaveFavoritesAsync(_favorites.ToArray(), FavoriteFolders.Append(new(Guid.NewGuid().ToString("N"), FolderName(), [])).ToArray(), selectLast: true), Editable);
        RenameFavoriteFolderCommand = new(() => SaveFavoritesAsync(_favorites.ToArray(), FavoriteFolders.Select(f => f.Id == SelectedFavoriteFolder?.Id ? f with { Name = FolderName() } : f).ToArray()), Editable);
        DeleteFavoriteFolderCommand = new(DeleteFavoriteFolderAsync, () => Editable() && FavoriteFolders.Count > 1);
        RemoveSelectedFavoritesCommand = new(RemoveSelectedFavoritesAsync, Editable);
        ImportFavoriteShareCommand = new(ImportFavoriteShareAsync, Editable);
        SelectAllFavoritesCommand = new(() => { var rows = FilteredFavoriteProjects; var select = rows.Any(r => !r.IsSelected); foreach (var row in rows) row.IsSelected = select; });
    }
    private string FolderName() => string.IsNullOrWhiteSpace(FavoriteFolderName) ? Localization.UiLocalization.Instance.Text("新收藏夹") : FavoriteFolderName.Trim();
    private void NotifyFavoriteFilter() { OnPropertyChanged(nameof(FilteredFavoriteProjects)); OnPropertyChanged(nameof(HasNoFavoriteMatches)); }
    private void LoadFavorites()
    {
        try
        {
            var path = FavoritesPath; SafePath.RejectLinks(path);
            if (!File.Exists(path)) { FavoriteFolders.Add(new("default", Localization.UiLocalization.Instance.Text("默认收藏夹"), [])); SelectedFavoriteFolder = FavoriteFolders[0]; FavoriteStatus = "尚未收藏任何资源。"; return; }
            if (new FileInfo(path).Length > 1048576) throw new IOException("收藏文件超过大小限制。");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var document = json.RootElement.ValueKind == JsonValueKind.Array
                ? new FavoriteDocument(1, json.RootElement.Deserialize<ContentProject[]>() ?? [], [])
                : json.RootElement.Deserialize<FavoriteDocument>() ?? throw new IOException("收藏文件格式无效。");
            var items = document.Projects; ValidateFavoriteProjects(items);
            if (document.Version is not (1 or 2) || document.Folders is null || document.Folders.Any(f => f is null || string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Name) || f.Projects is null || f.Projects.Any(id => !items.Any(p => p.Id == id))) || document.Folders.Select(f => f.Id).Distinct().Count() != document.Folders.Length) throw new IOException("收藏分组格式无效。");
            _favorites.AddRange(items.DistinctBy(p => p.Id));
            foreach (var folder in document.Folders) FavoriteFolders.Add(folder);
            if (FavoriteFolders.Count == 0) FavoriteFolders.Add(new("default", Localization.UiLocalization.Instance.Text("默认收藏夹"), items.Select(p => p.Id).ToArray()));
            SelectedFavoriteFolder = FavoriteFolders[0]; RefreshFavorites();
        }
        catch (Exception ex) { _favoritesLoadFailed = true; FavoriteStatus = "收藏读取失败，原文件已保留：" + UiPrivacy.Redact(ex.Message); }
    }
    private static void ValidateFavoriteProjects(ContentProject[] items)
    {
        if (items is null || items.Length > 1000 || items.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.PageUri is null || !p.PageUri.IsAbsoluteUri || p.PageUri.Scheme != "https" || !p.PageUri.IsDefaultPort || p.PageUri.UserInfo.Length != 0 || (p.Source == "curseforge" ? !p.Id.StartsWith("cf-", StringComparison.Ordinal) || p.PageUri.Host is not ("www.curseforge.com" or "curseforge.com") : p.Source != "modrinth" || p.PageUri.Host != "modrinth.com"))) throw new IOException("收藏文件格式无效。");
    }
    private void RefreshFavorites()
    {
        ClearProjectRows(FavoriteProjects); foreach (var project in _favorites) FavoriteProjects.Add(new(this, project));
        FavoriteStatus = _favorites.Count == 0 ? "尚未收藏任何资源。" : $"已保存 {_favorites.Count} 项收藏。";
        foreach (var property in new[] { nameof(HasFavorites), nameof(IsProjectFavorite), nameof(FavoriteAction) }) OnPropertyChanged(property);
        NotifyFavoriteFilter(); RestoreFavoriteIcons();
    }
    private async Task<bool> SaveFavoritesAsync(ContentProject[] projects, FavoriteFolder[] folders, bool selectLast = false)
    {
        try { ValidateFavoriteProjects(projects); } catch (IOException ex) { Owner.ShowError(ex.Message); return false; }
        var selected = selectLast ? folders.Last().Id : SelectedFavoriteFolder?.Id;
        if (!await Owner.RunAsync("保存收藏", async ct =>
        {
            var path = FavoritesPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); SafePath.RejectLinks(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { var json = JsonSerializer.SerializeToUtf8Bytes(new FavoriteDocument(2, projects, folders)); if (json.Length > 1048576) throw new IOException("收藏文件超过大小限制。"); await File.WriteAllBytesAsync(temporary, json, ct); ct.ThrowIfCancellationRequested(); SafePath.RejectLinks(path); SafePath.RejectLinks(path + ".bak"); if (File.Exists(path)) File.Copy(path, path + ".bak", true); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        })) { FavoriteStatus = Owner.ErrorMessage ?? "保存收藏已取消。"; return false; }
        _favorites.Clear(); _favorites.AddRange(projects); FavoriteFolders.Clear(); foreach (var f in folders) FavoriteFolders.Add(f);
        SelectedFavoriteFolder = FavoriteFolders.FirstOrDefault(f => f.Id == selected) ?? FavoriteFolders.FirstOrDefault(); RefreshFavorites(); NotifyCommands(); return true;
    }
    private async Task ToggleFavoriteAsync()
    {
        if (_selectedProject is not { } project) return;
        var remove = IsProjectFavorite; var folderId = SelectedFavoriteFolder?.Id ?? "default";
        var folders = FavoriteFolders.Select(f => f.Id != folderId ? f : f with { Projects = remove ? f.Projects.Where(id => id != project.Id).ToArray() : f.Projects.Append(project.Id).Distinct().ToArray() }).ToArray();
        var remaining = folders.SelectMany(f => f.Projects).ToHashSet();
        await SaveFavoritesAsync(_favorites.Append(project).DistinctBy(p => p.Id).Where(p => remaining.Contains(p.Id)).ToArray(), folders);
    }

    private async Task DeleteFavoriteFolderAsync()
    {
        if (SelectedFavoriteFolder is not { } folder || FavoriteFolders.Count <= 1) return;
        // Move contents to the remaining first folder so deleting a folder never deletes favorites.
        var folders = FavoriteFolders.Where(f => f.Id != folder.Id).ToArray(); folders[0] = folders[0] with { Projects = folders[0].Projects.Concat(folder.Projects).Distinct().ToArray() };
        await SaveFavoritesAsync(_favorites.ToArray(), folders);
    }
    private async Task RemoveSelectedFavoritesAsync()
    {
        var ids = FilteredFavoriteProjects.Where(r => r.IsSelected).Select(r => r.Project.Id).ToHashSet(); if (ids.Count == 0) return;
        var folders = FavoriteFolders.Select(f => f.Id == SelectedFavoriteFolder?.Id ? f with { Projects = f.Projects.Where(id => !ids.Contains(id)).ToArray() } : f).ToArray();
        var remaining = folders.SelectMany(f => f.Projects).ToHashSet(); await SaveFavoritesAsync(_favorites.Where(p => remaining.Contains(p.Id)).ToArray(), folders);
    }
    public string GetFavoriteShareCode()
    {
        var rows = FavoriteProjects.Where(r => SelectedFavoriteFolder?.Projects.Contains(r.Project.Id) ?? true).ToArray(); var chosen = rows.Any(r => r.IsSelected) ? rows.Where(r => r.IsSelected) : rows;
        return JsonSerializer.Serialize(chosen.Select(r => r.Project.Source == "curseforge" ? r.Project.Id[3..] : r.Project.Id).Distinct());
    }
    private async Task ImportFavoriteShareAsync()
    {
        if (_catalog is not IContentDependencyCatalogService catalog) { FavoriteStatus = "当前资源服务不支持读取分享项目。"; return; }
        var target = SelectedFavoriteFolder?.Id; var additions = new List<ContentProject>(); var failures = new List<string>();
        if (!await Owner.RunAsync("导入收藏", async ct =>
        {
            if (FavoriteShareCode.Length > 1048576) throw new InvalidDataException("分享内容过大。");
            var ids = JsonSerializer.Deserialize<string[]>(FavoriteShareCode) ?? throw new InvalidDataException("分享内容须为 WPF 收藏 ID 数组。");
            if (ids.Length > 1000 || ids.Any(id => string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) throw new InvalidDataException("收藏 ID 无效。");
            foreach (var id in ids.Distinct())
            {
                ct.ThrowIfCancellationRequested(); var canonical = id.All(char.IsAsciiDigit) ? "cf-" + id : id;
                if (_favorites.FirstOrDefault(p => p.Id == canonical) is { } saved) { additions.Add(saved); continue; }
                try { var project = await catalog.GetContentProjectAsync(canonical, ct); if (project.Id != canonical) throw new InvalidDataException("资源身份不符。"); ValidateFavoriteProjects([project]); additions.Add(project); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or InvalidOperationException) { failures.Add(id); }
            }
        })) return;
        if (additions.Count > 0) await SaveFavoritesAsync(_favorites.Concat(additions).DistinctBy(p => p.Id).ToArray(), FavoriteFolders.Select(f => f.Id == target ? f with { Projects = f.Projects.Concat(additions.Select(p => p.Id)).Distinct().ToArray() } : f).ToArray());
        FavoriteStatus = $"已导入 {additions.Count} 项收藏。" + (failures.Count > 0 ? " 无法读取：" + string.Join(", ", failures) : "");
    }
}
