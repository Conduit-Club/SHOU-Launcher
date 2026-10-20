using System.Windows.Input;
using PCL.Core.Conduit;

namespace PCL;

public partial class PageSetupUpdate
{
    public PageSetupUpdate()
    {
        InitializeComponent();
        CurrentVersion.Text = "Plain Craft Launcher 潮涌核心社版\n" + ClubRelease.CurrentTag;
    }
    private void Check_Click(object sender, MouseButtonEventArgs e) => CheckUpdate();
    public async void CheckUpdate()
    {
        if (!BtnCheckAgain.IsEnabled) return;
        BtnCheckAgain.IsEnabled = false;
        StatusText.Text = "正在检查社团 GitHub Releases…";
        try
        {
            var release = await ClubUpdates.CheckAsync();
            StatusText.Text = release is null ? "社团仓库暂未发布版本。" : release.IsNewerThan(ClubRelease.CurrentTag)
                ? "发现社团新版本：" + release.Title : "当前版本已是最新，或领先于已发布版本。";
            ReleaseNotes.Text = release is null ? "" : release.Title + "\n\n" + release.Notes;
        }
        catch (Exception ex)
        {
            StatusText.Text = "检查失败：无法读取 GitHub（可能是网络或 API 额度限制）。可直接打开下载页面。";
            ModBase.Log(ex, "[Conduit] 检查社团更新失败");
        }
        finally { BtnCheckAgain.IsEnabled = true; }
    }
    private void Download_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenWebsite(ClubUpdates.Latest?.PageUrl ?? ClubCatalog.Repository + "/releases");
}
