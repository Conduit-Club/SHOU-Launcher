using System.Windows.Input;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubSourceSettings : UserControl
{
    private bool _loaded;
    private bool _loading;
    public event EventHandler? Saved;
    private ClubSourceCategorySettings[] Pages => [MinecraftSources, ModSources, LoaderSources];

    public ClubSourceSettings()
    {
        InitializeComponent();
        foreach (var page in Pages) page.Edited += (_, _) =>
        {
            if (_loading) return;
            PendingStatus.Text = "有未保存的修改 · 点击保存后生效";
            Status.Text = "";
        };
        Loaded += (_, _) =>
        {
            // Tab changes and returning to this page must not discard pending edits.
            if (_loaded) return;
            _loaded = true;
            ReloadFromConfig();
        };
    }

    public void ReloadFromConfig()
    {
        _loading = true;
        try
        {
            List<DownloadSource> sources;
            try { sources = DownloadSources.Parse(Config.Download.Sources); }
            catch (Exception ex) { sources = DownloadSources.Defaults(); Status.Text = "配置无效，已载入默认值供修复：" + ex.Message; }
            UseOrder.Checked = Config.Download.UseSourceOrder;
            foreach (var page in Pages) page.LoadSources(sources);
            PendingStatus.Text = "没有未保存的修改";
            UpdatePolicy();
        }
        finally { _loading = false; }
    }

    private void ChangeMode(object sender, bool user)
    {
        if (_loading || !_loaded) return;
        if (UseOrder.Checked == true)
        {
            try { DownloadSources.Parse(Config.Download.Sources); }
            catch
            {
                _loading = true;
                UseOrder.Checked = Config.Download.UseSourceOrder;
                _loading = false;
                Status.Text = "请先修正并保存有效的源配置，再启用自定义顺序。";
                return;
            }
        }
        Config.Download.UseSourceOrder = UseOrder.Checked == true;
        ClubSourceRequests.InvalidateManifest();
        UpdatePolicy();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePolicy()
    {
        var enabled = Config.Download.UseSourceOrder;
        List<DownloadSource> sources;
        try { sources = DownloadSources.Parse(Config.Download.Sources); }
        catch { EffectivePolicy.Text = "已保存的源配置无效，请修正后保存；当前编辑区载入默认值供修复。"; return; }
        var order = string.Join(" → ", sources.Where(s => s.Enabled && s.Category == SourceCategory.Minecraft).OrderBy(s => s.Priority).Select(s => s.Name));
        EffectivePolicy.Text = enabled
            ? $"当前模式：自定义源顺序。Minecraft 文件：{order}\n版本浏览优先完整清单，社团精选清单仅作备用。Mod 与加载器按各自分类设置回退。"
            : "当前使用下方常规下载设置；这里的源顺序暂不生效，未保存的修改仍保留。";
    }

    private void SaveSources(object sender, MouseButtonEventArgs e)
    {
        var sources = new List<DownloadSource>();
        for (var index = 0; index < Pages.Length; index++)
        {
            try { sources.AddRange(Pages[index].GetValidatedSources()); }
            catch (Exception ex)
            {
                CategoryTabs.SelectedIndex = index;
                Status.Text = Pages[index].CategoryName + "：" + ex.Message;
                return;
            }
        }
        try
        {
            Config.Download.Sources = JsonSerializer.Serialize(sources);
            Config.Download.UseSourceOrder = UseOrder.Checked == true;
            ClubSourceRequests.InvalidateManifest();
            _loading = true;
            try { foreach (var page in Pages) page.LoadSources(sources); }
            finally { _loading = false; }
            PendingStatus.Text = "没有未保存的修改";
            UpdatePolicy();
            Saved?.Invoke(this, EventArgs.Empty);
            Status.Text = Config.Download.UseSourceOrder
                ? "已保存下载源并清除旧版本列表缓存。版本浏览优先完整清单；文件按设置顺序下载。"
                : "已保存源配置；自定义顺序尚未启用，当前仍使用常规下载设置。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
