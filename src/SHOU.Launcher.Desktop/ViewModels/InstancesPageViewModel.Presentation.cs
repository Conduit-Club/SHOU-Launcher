using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record InstanceIconChoice(string Id, string Title);

public sealed partial class InstancesPageViewModel
{
    private InstanceIconChoice _presentationIcon = null!;
    private string _presentationCategory = "自动", _presentationDescription = "";
    private string? _presentationInstanceId;
    private bool _presentationDirty, _presentationRefreshing, _descriptionEditorOpen, _presentationFavorite;
    public IReadOnlyList<InstanceIconChoice> InstanceIcons { get; } = [new("auto", "自动"), new("minecraft", "Minecraft"), new("fabric", "Fabric"), new("quilt", "Quilt"), new("forge", "Forge"), new("neoforge", "NeoForge")];
    public ObservableCollection<string> InstanceCategories { get; } = ["自动", "常用", "整合包", "测试"];
    public InstanceIconChoice PresentationIcon { get => _presentationIcon; set { if (value is not null && SetProperty(ref _presentationIcon, value) && !_presentationRefreshing) PresentationChanged(); } }
    public string PresentationCategory { get => _presentationCategory; set { if (SetProperty(ref _presentationCategory, value ?? "自动") && !_presentationRefreshing) PresentationChanged(); } }
    public string PresentationDescription { get => _presentationDescription; set { if (SetProperty(ref _presentationDescription, value ?? "") && !_presentationRefreshing) PresentationChanged(); } }
    public bool IsDescriptionEditorOpen { get => _descriptionEditorOpen; private set => SetProperty(ref _descriptionEditorOpen, value); }
    public string PresentationSummary => _presentationDirty ? "个性化有未保存的修改。" : SelectedInstance?.Presentation?.Description ?? "";
    public string FavoriteCaption => _presentationFavorite ? "移出收藏夹" : "加入收藏夹";
    public string LaunchCountLabel => (SelectedInstance?.Presentation?.LaunchCount ?? 0).ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " 次";
    public string InstanceIconPath => Themes.ClubIcons.ForInstanceIcon(PresentationIcon?.Id == "auto" ? SelectedInstance?.Loader : PresentationIcon?.Id);
    public bool FavoritesOnly { get => _favoritesOnly; set { if (SetProperty(ref _favoritesOnly, value)) FilterInstances(); } }
    private bool _favoritesOnly;
    public AsyncRelayCommand SavePresentationCommand { get; private set; } = null!;
    public RelayCommand EditDescriptionCommand { get; private set; } = null!;
    public RelayCommand CancelDescriptionCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleFavoriteCommand { get; private set; } = null!;
    private bool CanEditPresentation => CanEdit && SelectedInstance is not null && Service is IInstancePresentationService;
    private void InitializePresentation()
    {
        _presentationIcon = InstanceIcons[0];
        SavePresentationCommand = AsyncCommand(SavePresentationAsync, () => CanEditPresentation && SelectedInstance?.Id == _presentationInstanceId);
        EditDescriptionCommand = Command(() => IsDescriptionEditorOpen = true, () => CanEditPresentation);
        CancelDescriptionCommand = Command(() => { IsDescriptionEditorOpen = false; _presentationDirty = false; RefreshPresentation(); });
        ToggleFavoriteCommand = AsyncCommand(async () => { _presentationFavorite = !_presentationFavorite; PresentationChanged(); await SavePresentationAsync(); }, () => CanEditPresentation);
    }
    private void PresentationChanged()
    {
        _presentationDirty = true;
        OnPropertyChanged(nameof(PresentationSummary)); OnPropertyChanged(nameof(FavoriteCaption)); OnPropertyChanged(nameof(InstanceIconPath));
    }
    private void RefreshPresentation()
    {
        if (SelectedInstance?.Id != _presentationInstanceId || !_presentationDirty)
        {
            _presentationRefreshing = true;
            try
            {
                if (SelectedInstance?.Id != _presentationInstanceId) IsDescriptionEditorOpen = false;
                _presentationInstanceId = SelectedInstance?.Id;
                var value = SelectedInstance?.Presentation ?? new();
                PresentationIcon = InstanceIcons.FirstOrDefault(i => i.Id == value.Icon) ?? InstanceIcons[0];
                var category = value.Category == "auto" ? "自动" : value.Category;
                if (!InstanceCategories.Contains(category)) InstanceCategories.Add(category);
                PresentationCategory = category; PresentationDescription = value.Description; _presentationFavorite = value.Favorite; _presentationDirty = false;
            }
            finally { _presentationRefreshing = false; }
        }
        foreach (var name in new[] { nameof(PresentationSummary), nameof(FavoriteCaption), nameof(LaunchCountLabel), nameof(InstanceIconPath) }) OnPropertyChanged(name);
    }
    private async Task SavePresentationAsync()
    {
        var target = SelectedInstance!.Id;
        var edit = new InstancePresentationEdit(PresentationIcon.Id, PresentationCategory == "自动" ? "auto" : PresentationCategory.Trim(), PresentationDescription, _presentationFavorite);
        if (await Owner.RunAsync("保存实例个性化", ct => ((IInstancePresentationService)Service).UpdateInstancePresentationAsync(target, edit, ct)) && SelectedInstance?.Id == target)
        {
            _presentationDirty = false; IsDescriptionEditorOpen = false; RefreshPresentation(); FilterInstances(); Feedback = "实例个性化已保存。";
        }
    }
    private static string DisplayGameVersion(GameInstance instance)
    {
        try { return InstanceIdentity.GameVersion(instance); }
        catch (InvalidDataException) { return instance.VersionId; }
    }
}
