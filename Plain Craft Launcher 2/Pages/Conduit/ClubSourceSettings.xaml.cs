using System.Windows.Input;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Conduit;

namespace PCL;

public partial class ClubSourceSettings : UserControl
{
    private bool _loaded;
    private ClubSourceCategorySettings[] Pages => [MinecraftSources, ModSources, LoaderSources];

    public ClubSourceSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Tab changes and returning to this page must not discard pending edits.
            if (_loaded) return;
            _loaded = true;
            List<DownloadSource> sources;
            try { sources = DownloadSources.Parse(Config.Download.Sources); }
            catch (Exception ex) { sources = DownloadSources.Defaults(); Status.Text = "配置无效，已载入默认值供修复：" + ex.Message; }
            UseOrder.Checked = Config.Download.UseSourceOrder;
            foreach (var page in Pages) page.LoadSources(sources);
        };
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
            if (ModDownload.dlClientListLoader.State != ModBase.LoadState.Loading)
                ModDownload.dlClientListLoader.State = ModBase.LoadState.Waiting;
            foreach (var page in Pages) page.LoadSources(sources);
            Status.Text = "已保存三个分类的下载源，新下载任务生效；已打开的加载器列表可通过刷新重新获取。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
