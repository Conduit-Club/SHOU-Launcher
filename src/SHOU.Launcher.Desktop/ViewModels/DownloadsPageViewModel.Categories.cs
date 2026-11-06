using System.Collections.ObjectModel;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record ContentCategoryChoice(string Id, string Name);
public sealed partial class DownloadsPageViewModel
{
    private IReadOnlyList<ContentCategory>? _contentCategories;
    private ContentCategoryChoice _contentCategory = new("", "全部");
    private string _categoryStatus = "";
    public ObservableCollection<ContentCategoryChoice> ContentCategoryChoices { get; } = [new("", "全部")];
    public bool HasContentCategoryChoices => ContentCategoryChoices.Count > 1;
    public string CategoryStatus { get => _categoryStatus; private set => SetProperty(ref _categoryStatus, value); }
    public ContentCategoryChoice SelectedContentCategory
    {
        get => _contentCategory;
        set { if (value is not null && SetProperty(ref _contentCategory, value)) FiltersChanged(); }
    }
    private void UpdateContentCategories()
    {
        var wasRestoring = _restoringSearch; _restoringSearch = true;
        try {
        var selected = _contentCategory.Id;
        ContentCategoryChoices.Clear(); ContentCategoryChoices.Add(new("", "全部"));
        var kind = SelectedCategory.Id == "datapack" ? "mod" : SelectedCategory.Id;
        foreach (var tag in (_contentCategories ?? []).Where(c => c.Kind == kind).OrderBy(c => c.Id, StringComparer.Ordinal))
            if (!LoaderFilters.Any(l => l.Equals(tag.Id, StringComparison.OrdinalIgnoreCase)) && tag.Id != "datapack") ContentCategoryChoices.Add(new(tag.Id, CategoryName(tag.Id)));
        _contentCategory = ContentCategoryChoices.FirstOrDefault(c => c.Id == selected) ?? ContentCategoryChoices[0];
        OnPropertyChanged(nameof(SelectedContentCategory)); OnPropertyChanged(nameof(HasContentCategoryChoices));
        } finally { _restoringSearch = wasRestoring; }
    }
    private async Task ReadContentCategoriesAsync(int revision)
    {
        if (_contentCategories is not null) { UpdateContentCategories(); return; }
        if ((_catalog as IContentCategoryCatalogService ?? Service as IContentCategoryCatalogService) is not { } service) return;
        await ReadBrowseAsync("获取资源标签", revision, async ct =>
        {
            var categories = await service.GetContentCategoriesAsync(ct); ct.ThrowIfCancellationRequested();
            if (_disposed) return;
            _contentCategories = categories; CategoryStatus = "";
            if (IsSearchPage) UpdateContentCategories();
        }, "categories", page: false, failure: message => CategoryStatus = message);
    }
    internal static string CategoryName(string id) => id switch
    {
        "optimization" => "性能优化", "library" => "支持库", "adventure" => "冒险", "decoration" => "装饰",
        "worldgen" => "世界生成", "technology" => "科技", "magic" => "魔法", "utility" => "实用", "management" => "管理",
        "storage" => "存储", "mobs" => "生物", "equipment" => "装备", "transportation" => "交通", "food" => "食物",
        "game-mechanics" => "游戏机制", "social" => "社交", _ => id
    };
}
