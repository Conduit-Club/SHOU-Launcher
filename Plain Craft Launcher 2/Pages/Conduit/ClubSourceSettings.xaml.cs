using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubSourceSettings : UserControl
{
    private List<DownloadSource> _sources = [];
    private DownloadSource? _selected;
    private bool _refreshing;
    public ClubSourceSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try { _sources = DownloadSources.Parse(Config.Download.Sources); }
            catch (Exception ex) { _sources = DownloadSources.Defaults(); Status.Text = "配置无效，已载入默认值供修复：" + ex.Message; }
            UseOrder.Checked = Config.Download.UseSourceOrder;
            RefreshList();
        };
    }

    private void RefreshList()
    {
        _refreshing = true;
        _sources = _sources.OrderBy(s => s.Priority).ToList();
        Sources.Items.Clear();
        foreach (var s in _sources) Sources.Items.Add(new ListBoxItem { Content = $"{s.Priority} · {s.Name} [{s.Category}] {(s.Enabled ? "" : "（停用）")}", Tag = s });
        _selected = null;
        _refreshing = false;
        if (Sources.Items.Count > 0) Sources.SelectedIndex = 0;
        Editor.IsEnabled = Sources.Items.Count > 0;
    }

    private void SelectSource(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        // Preserve all edits before moving to another row.
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
        Category.SelectedIndex = (int)_selected.Category;
        Layout.SelectedIndex = (int)_selected.Layout;
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
        _selected.Category = (SourceCategory)Category.SelectedIndex;
        _selected.Layout = (SourceLayout)Layout.SelectedIndex;
        _selected.BaseUrl = BaseUrl.Text.Trim().TrimEnd('/');
        _selected.OriginPrefix = Origin.Text.Trim().TrimEnd('/');
        _selected.Priority = priority;
        _selected.Enabled = Enabled.Checked == true;
        return true;
    }

    private void AddSource(object sender, RoutedEventArgs e)
    {
        if (!ReadEditor()) return;
        _sources.Add(new DownloadSource());
        var added = _sources.Last();
        RefreshList();
        Sources.SelectedItem = Sources.Items.Cast<ListBoxItem>().First(i => ReferenceEquals(i.Tag, added));
    }

    private void DeleteSource(object sender, RoutedEventArgs e)
    {
        if (_selected != null) _sources.Remove(_selected);
        RefreshList();
    }

    private void ResetSources(object sender, RoutedEventArgs e) { _sources = DownloadSources.Defaults(); RefreshList(); Status.Text = "已载入默认值；保存后生效。"; }

    private void SaveSources(object sender, RoutedEventArgs e)
    {
        if (!ReadEditor()) return;
        try
        {
            foreach (var source in _sources) DownloadSources.Validate(source);
            foreach (var category in Enum.GetValues<SourceCategory>())
                if (!_sources.Any(s => s.Enabled && s.Category == category)) throw new ArgumentException($"{category} 至少需要一个启用的源。");
            Config.Download.Sources = JsonSerializer.Serialize(_sources);
            Config.Download.UseSourceOrder = UseOrder.Checked == true;
            if (ModDownload.dlClientListLoader.State != ModBase.LoadState.Loading)
                ModDownload.dlClientListLoader.State = ModBase.LoadState.Waiting;
            RefreshList();
            Status.Text = "已保存，新下载任务生效；已打开的加载器列表可通过刷新重新获取。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
