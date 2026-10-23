using System.Windows.Input;
using System.Windows.Controls;
using System.Windows;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubSourceCategorySettings : UserControl
{
    private List<DownloadSource> _sources = [];
    private DownloadSource? _selected;
    private bool _refreshing;
    public event EventHandler? Edited;
    public SourceCategory Category { get; set; }
    public string CategoryName => Category switch
    {
        SourceCategory.Minecraft => "Minecraft 游戏本体",
        SourceCategory.Mod => "Mod / 整合包 / 资源",
        _ => "Mod Loader"
    };

    public ClubSourceCategorySettings()
    {
        InitializeComponent();
        foreach (var field in new[] { SourceName, BaseUrl, Origin, Priority })
            field.TextChanged += (_, _) => { if (!_refreshing) Edited?.Invoke(this, EventArgs.Empty); };
        Layout.SelectionChanged += (_, _) => { UpdateEditorFields(); if (!_refreshing) Edited?.Invoke(this, EventArgs.Empty); };
        Priority.LostKeyboardFocus += (_, _) => { if (!_refreshing && ReadEditor()) RefreshList(_selected); };
        SourceName.LostKeyboardFocus += (_, _) => { if (!_refreshing && ReadEditor()) RefreshList(_selected); };
    }

    public void LoadSources(IEnumerable<DownloadSource> sources)
    {
        var previous = _selected;
        _refreshing = true;
        _sources = sources.Where(s => s.Category == Category).ToList();
        CategoryHint.Text = Category switch
        {
            SourceCategory.Minecraft => "文件按列表从上到下回退。版本浏览优先完整清单；社团 SHOU-MCMirror 仅提供精选版本清单，作为备用。",
            SourceCategory.Mod => "管理 Mod、整合包与资源的 API / 文件镜像；可以分别添加 Modrinth 与 CurseForge 的前缀规则。",
            _ => "管理 Fabric、Forge、NeoForge、Quilt 等加载器的元数据与安装文件。"
        };
        Layout.Items.Clear();
        foreach (var layout in Enum.GetValues<SourceLayout>())
        {
            if (layout == SourceLayout.Shou && Category != SourceCategory.Minecraft) continue;
            if (layout == SourceLayout.Bmclapi && Category == SourceCategory.Mod) continue;
            Layout.Items.Add(new MyComboBoxItem { Content = layout switch
            {
                SourceLayout.Official => "官方地址",
                SourceLayout.Bmclapi => "BMCLAPI 兼容",
                SourceLayout.Shou => "SHOU-MCMirror",
                _ => "自定义 URL 前缀替换"
            }, Tag = layout });
        }
        RefreshList(previous);
    }

    private void RefreshList(DownloadSource? preferred = null)
    {
        _refreshing = true;
        _sources = _sources.OrderBy(s => s.Priority).ToList();
        Sources.Items.Clear();
        foreach (var s in _sources)
        {
            var row = new StackPanel();
            var enabled = new MyCheckBox { Text = s.Name, Checked = s.Enabled, MinHeight = 28 };
            enabled.Change += (_, _) =>
            {
                s.Enabled = enabled.Checked == true;
                Sources.SelectedItem = Sources.Items.Cast<ListBoxItem>().First(i => ReferenceEquals(i.Tag, s));
                Edited?.Invoke(this, EventArgs.Empty);
            };
            row.Children.Add(enabled);
            row.Children.Add(new TextBlock { Text = s.Layout == SourceLayout.Shou ? "精选版本备用清单 · 文件按此顺序回退" : s.Layout == SourceLayout.Official ? "官方接口与文件" : s.BaseUrl,
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 5) });
            Sources.Items.Add(new ListBoxItem { Content = row, Tag = s });
        }
        _selected = null;
        _refreshing = false;
        if (Sources.Items.Count > 0) Sources.SelectedItem = Sources.Items.Cast<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, preferred)) ?? Sources.Items[0];
        Editor.IsEnabled = Sources.Items.Count > 0;
        UpdateMoveButtons();
    }

    private void SelectSource(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (_selected != null && !ReadEditor())
        {
            _refreshing = true;
            Sources.SelectedItem = Sources.Items.Cast<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, _selected));
            _refreshing = false;
            return;
        }
        _selected = (Sources.SelectedItem as ListBoxItem)?.Tag as DownloadSource;
        UpdateMoveButtons();
        if (_selected == null) return;
        _refreshing = true;
        SourceName.Text = _selected.Name;
        Layout.SelectedItem = Layout.Items.Cast<MyComboBoxItem>().First(i => (SourceLayout)i.Tag == _selected.Layout);
        BaseUrl.Text = _selected.BaseUrl;
        Origin.Text = _selected.OriginPrefix;
        Priority.Text = _selected.Priority.ToString();
        _refreshing = false;
        UpdateEditorFields();
    }

    private bool ReadEditor()
    {
        if (_selected == null) return true;
        if (!int.TryParse(Priority.Text, out var priority)) { AdvancedEditor.IsExpanded = true; Status.Text = "优先级必须为整数。"; return false; }
        _selected.Name = SourceName.Text.Trim();
        _selected.Layout = (SourceLayout)((MyComboBoxItem)Layout.SelectedItem).Tag;
        _selected.BaseUrl = BaseUrl.Text.Trim().TrimEnd('/');
        _selected.OriginPrefix = Origin.Text.Trim().TrimEnd('/');
        _selected.Priority = priority;
        Status.Text = "";
        return true;
    }

    public IReadOnlyList<DownloadSource> GetValidatedSources()
    {
        if (!ReadEditor()) { AdvancedEditor.IsExpanded = true; throw new ArgumentException(Status.Text); }
        try
        {
            foreach (var source in _sources)
            {
                try { DownloadSources.Validate(source); }
                catch { RefreshList(source); throw; }
            }
            if (!_sources.Any(s => s.Enabled)) throw new ArgumentException("至少需要一个启用的源。");
        }
        catch { AdvancedEditor.IsExpanded = true; throw; }
        return _sources;
    }

    private void AddSource(object sender, MouseButtonEventArgs e)
    {
        if (!ReadEditor()) return;
        var added = new DownloadSource { Category = Category };
        _sources.Add(added);
        RefreshList(added);
        AdvancedEditor.IsExpanded = true;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSource(object sender, MouseButtonEventArgs e)
    {
        if (_selected != null) _sources.Remove(_selected);
        RefreshList();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void ResetSources(object sender, MouseButtonEventArgs e)
    {
        LoadSources(DownloadSources.Defaults());
        Status.Text = "已恢复本页默认值，其他分类不变；保存后生效。";
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMoveButtons()
    {
        MoveUpButton.IsEnabled = Sources.SelectedIndex > 0;
        MoveDownButton.IsEnabled = Sources.SelectedIndex >= 0 && Sources.SelectedIndex < Sources.Items.Count - 1;
    }

    private void UpdateEditorFields()
    {
        if (Layout.SelectedItem is not MyComboBoxItem item) return;
        var layout = (SourceLayout)item.Tag;
        BaseUrl.Visibility = BaseUrlLabel.Visibility = layout == SourceLayout.Official ? Visibility.Collapsed : Visibility.Visible;
        Origin.Visibility = OriginLabel.Visibility = layout == SourceLayout.Prefix ? Visibility.Visible : Visibility.Collapsed;
        Editor.RowDefinitions[2].Height = new GridLength(layout == SourceLayout.Official ? 0 : 36);
        Editor.RowDefinitions[3].Height = new GridLength(layout == SourceLayout.Prefix ? 36 : 0);
    }

    private void EditSource(object sender, MouseButtonEventArgs e) { AdvancedEditor.IsExpanded = true; SourceName.Focus(); }
    private void MoveUp(object sender, MouseButtonEventArgs e) => Move(-1);
    private void MoveDown(object sender, MouseButtonEventArgs e) => Move(1);
    private void Move(int delta)
    {
        if (!ReadEditor() || _selected is null) return;
        var index = _sources.IndexOf(_selected);
        var target = index + delta;
        if (target < 0 || target >= _sources.Count) return;
        (_sources[index], _sources[target]) = (_sources[target], _sources[index]);
        for (var i = 0; i < _sources.Count; i++) _sources[i].Priority = i * 100;
        RefreshList(_selected);
        Edited?.Invoke(this, EventArgs.Empty);
    }
}
