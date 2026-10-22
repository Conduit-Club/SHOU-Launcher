using System.Windows.Input;
using System.Windows.Controls;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubSourceCategorySettings : UserControl
{
    private List<DownloadSource> _sources = [];
    private DownloadSource? _selected;
    private bool _refreshing;
    public SourceCategory Category { get; set; }
    public string CategoryName => Category switch
    {
        SourceCategory.Minecraft => "Minecraft 游戏本体",
        SourceCategory.Mod => "Mod / 整合包 / 资源",
        _ => "Mod Loader"
    };

    public ClubSourceCategorySettings() => InitializeComponent();

    public void LoadSources(IEnumerable<DownloadSource> sources)
    {
        _sources = sources.Where(s => s.Category == Category).ToList();
        CategoryHint.Text = Category switch
        {
            SourceCategory.Minecraft => "管理游戏版本清单、客户端、依赖库与资源。社团 SHOU-MCMirror 只提供选定的 Minecraft 版本。",
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
        RefreshList();
    }

    private void RefreshList()
    {
        _refreshing = true;
        _sources = _sources.OrderBy(s => s.Priority).ToList();
        Sources.Items.Clear();
        foreach (var s in _sources) Sources.Items.Add(new ListBoxItem { Content = $"{s.Priority} · {s.Name} {(s.Enabled ? "" : "（停用）")}", Tag = s });
        _selected = null;
        _refreshing = false;
        if (Sources.Items.Count > 0) Sources.SelectedIndex = 0;
        Editor.IsEnabled = Sources.Items.Count > 0;
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
        if (_selected == null) return;
        SourceName.Text = _selected.Name;
        Layout.SelectedItem = Layout.Items.Cast<MyComboBoxItem>().First(i => (SourceLayout)i.Tag == _selected.Layout);
        BaseUrl.Text = _selected.BaseUrl;
        Origin.Text = _selected.OriginPrefix;
        Priority.Text = _selected.Priority.ToString();
        Enabled.Checked = _selected.Enabled;
    }

    private bool ReadEditor()
    {
        if (_selected == null) return true;
        if (!int.TryParse(Priority.Text, out var priority)) { Status.Text = "优先级必须为整数。"; return false; }
        _selected.Name = SourceName.Text.Trim();
        _selected.Layout = (SourceLayout)((MyComboBoxItem)Layout.SelectedItem).Tag;
        _selected.BaseUrl = BaseUrl.Text.Trim().TrimEnd('/');
        _selected.OriginPrefix = Origin.Text.Trim().TrimEnd('/');
        _selected.Priority = priority;
        _selected.Enabled = Enabled.Checked == true;
        Status.Text = "";
        return true;
    }

    public IReadOnlyList<DownloadSource> GetValidatedSources()
    {
        if (!ReadEditor()) throw new ArgumentException(Status.Text);
        foreach (var source in _sources) DownloadSources.Validate(source);
        if (!_sources.Any(s => s.Enabled)) throw new ArgumentException("至少需要一个启用的源。");
        return _sources;
    }

    private void AddSource(object sender, MouseButtonEventArgs e)
    {
        if (!ReadEditor()) return;
        var added = new DownloadSource { Category = Category };
        _sources.Add(added);
        RefreshList();
        Sources.SelectedItem = Sources.Items.Cast<ListBoxItem>().First(i => ReferenceEquals(i.Tag, added));
    }

    private void DeleteSource(object sender, MouseButtonEventArgs e)
    {
        if (_selected != null) _sources.Remove(_selected);
        RefreshList();
    }

    private void ResetSources(object sender, MouseButtonEventArgs e)
    {
        LoadSources(DownloadSources.Defaults());
        Status.Text = "已恢复本页默认值，其他分类不变；保存后生效。";
    }
}
