using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCL.Core.Conduit;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class SettingsSourceRow(DownloadSource source, Action changed) : ObservableObject
{
    public IReadOnlyList<SourceLayout> LayoutChoices { get; } = Enum.GetValues<SourceLayout>();
    public SourceCategory Category { get; } = source.Category;
    private string _name = source.Name;
    private SourceLayout _layout = source.Layout;
    private string _baseUrl = source.BaseUrl;
    private string _originPrefix = source.OriginPrefix;
    private int _priority = source.Priority;
    private bool _enabled = source.Enabled;
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) changed(); } }
    public SourceLayout Layout { get => _layout; set { if (SetProperty(ref _layout, value)) { changed(); OnPropertyChanged(nameof(IsPrefix)); OnPropertyChanged(nameof(IsAddressEditable)); } } }
    public string BaseUrl { get => _baseUrl; set { if (SetProperty(ref _baseUrl, value)) changed(); } }
    public string OriginPrefix { get => _originPrefix; set { if (SetProperty(ref _originPrefix, value)) changed(); } }
    public int Priority { get => _priority; set { if (SetProperty(ref _priority, value)) changed(); } }
    public bool Enabled { get => _enabled; set { if (SetProperty(ref _enabled, value)) changed(); } }
    public bool IsPrefix => Layout == SourceLayout.Prefix;
    public bool IsAddressEditable => Layout != SourceLayout.Official;
    public string Summary => $"{Layout} · 优先级 {Priority}";
    public DownloadSource ToSource() => new() { Name = Name, Category = Category, Layout = Layout, BaseUrl = BaseUrl, OriginPrefix = OriginPrefix, Priority = Priority, Enabled = Enabled };
}

public sealed partial class SettingsPageViewModel
{
    private bool _updatingSources;
    private bool _sourceEditorReady;
    private readonly ObservableCollection<SettingsSourceRow> _allSources = [];
    public ObservableCollection<SettingsSourceRow> Sources { get; } = [];
    public IReadOnlyList<SourceLayout> SourceLayouts { get; } = Enum.GetValues<SourceLayout>();
    private SourceCategory _selectedSourceCategory = SourceCategory.Minecraft;
    public SourceCategory SelectedSourceCategory
    {
        get => _selectedSourceCategory;
        set
        {
            if (!SetProperty(ref _selectedSourceCategory, value)) return;
            OnPropertyChanged(nameof(IsMinecraftSources)); OnPropertyChanged(nameof(IsModSources)); OnPropertyChanged(nameof(IsLoaderSources)); UpdateSourceCategory();
        }
    }
    public bool IsMinecraftSources => SelectedSourceCategory == SourceCategory.Minecraft;
    public bool IsModSources => SelectedSourceCategory == SourceCategory.Mod;
    public bool IsLoaderSources => SelectedSourceCategory == SourceCategory.Loader;
    private SettingsSourceRow? _selectedSource;
    public SettingsSourceRow? SelectedSource { get => _selectedSource; set { if (SetProperty(ref _selectedSource, value)) { OnPropertyChanged(nameof(HasSelectedSource)); NotifyCommands(); } } }
    public bool HasSelectedSource => SelectedSource is not null;
    private string _sourceEditorSummary = "留空沿用内置来源。";
    public string SourceEditorSummary { get => _sourceEditorSummary; private set => SetProperty(ref _sourceEditorSummary, value); }
    public RelayCommand<string> SelectSourceCategoryCommand { get; private set; } = null!;
    public RelayCommand MoveSourceUpCommand { get; private set; } = null!;
    public RelayCommand MoveSourceDownCommand { get; private set; } = null!;
    public RelayCommand AddSourceCommand { get; private set; } = null!;
    public RelayCommand RemoveSourceCommand { get; private set; } = null!;
    public RelayCommand ResetSourcesCommand { get; private set; } = null!;
    private void InitializeSourceEditor()
    {
        SelectSourceCategoryCommand = new(category => { if (Enum.TryParse<SourceCategory>(category, out var parsed)) SelectedSourceCategory = parsed; });
        MoveSourceUpCommand = Command(() => MoveSource(-1), () => CanEdit && SelectedSource is not null && Sources.IndexOf(SelectedSource) > 0);
        MoveSourceDownCommand = Command(() => MoveSource(1), () => CanEdit && SelectedSource is not null && Sources.IndexOf(SelectedSource) < Sources.Count - 1);
        AddSourceCommand = Command(() =>
        {
            var row = NewRow(new() { Name = "自定义源", Category = SelectedSourceCategory, Layout = SourceLayout.Prefix, BaseUrl = "", OriginPrefix = "", Priority = Sources.Count + 1, Enabled = true });
            _allSources.Add(row); UpdateSourceCategory(row); SyncSourcesJson();
        }, () => CanEdit);
        RemoveSourceCommand = Command(() => { _allSources.Remove(SelectedSource!); UpdateSourceCategory(); SyncSourcesJson(); }, () => CanEdit && SelectedSource is not null);
        ResetSourcesCommand = Command(() => { DownloadSourcesJson = ""; RefreshSourceEditor(""); MarkDirty(); }, () => CanEdit);
        _sourceEditorReady = true; RefreshSourceEditor(DownloadSourcesJson);
    }
    private SettingsSourceRow NewRow(DownloadSource source) => new(source, SyncSourcesJson);
    private void RefreshSourceEditor(string json)
    {
        if (!_sourceEditorReady || _updatingSources) return;
        try
        {
            var parsed = DownloadSources.Parse(json);
            _allSources.Clear(); foreach (var source in parsed) _allSources.Add(NewRow(source));
            UpdateSourceCategory(); SourceEditorSummary = string.IsNullOrWhiteSpace(json) ? "使用内置来源顺序。修改列表后点击保存设置生效。" : "已载入自定义来源。每类至少保留一个启用来源；保存设置后生效。";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { SourceEditorSummary = "来源草稿无效：" + UiPrivacy.Redact(ex.Message); }
    }
    private void UpdateSourceCategory(SettingsSourceRow? preferred = null)
    {
        var selected = preferred ?? SelectedSource;
        Sources.Clear(); foreach (var row in _allSources.Where(s => s.Category == SelectedSourceCategory).OrderBy(s => s.Priority)) Sources.Add(row);
        SelectedSource = Sources.Contains(selected!) ? selected : Sources.FirstOrDefault(); NotifyCommands();
    }
    private void MoveSource(int direction)
    {
        if (SelectedSource is not { } selected) return;
        var index = Sources.IndexOf(selected); var target = index + direction;
        if (index < 0 || target < 0 || target >= Sources.Count) return;
        _updatingSources = true;
        try { Sources.Move(index, target); for (var i = 0; i < Sources.Count; i++) Sources[i].Priority = i + 1; }
        finally { _updatingSources = false; }
        SyncSourcesJson(); NotifyCommands();
    }
    private void SyncSourcesJson()
    {
        if (_updatingSources || !_sourceEditorReady) return;
        _updatingSources = true;
        try
        {
            DownloadSourcesJson = JsonSerializer.Serialize(_allSources.Select(s => s.ToSource()).OrderBy(s => s.Category).ThenBy(s => s.Priority));
            SourceEditorSummary = "来源顺序有未保存修改，切换分类保留草稿。";
        }
        finally { _updatingSources = false; }
    }
}
