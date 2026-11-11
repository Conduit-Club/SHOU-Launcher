using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record AboutCredit(string Name, string Description, string ActionLabel, string? Page,
    Bitmap? Portrait, AsyncRelayCommand? OpenCommand)
{
    public bool HasPortrait => Portrait is not null;
    public bool HasAction => OpenCommand is not null;
}
public sealed partial class SettingsPageViewModel
{
    public IReadOnlyList<AboutCredit> OriginalCredits { get; private set; } = [];
    public IReadOnlyList<AboutCredit> ClubAcknowledgements { get; private set; } = [];
    public IReadOnlyList<AboutCredit> HistoricalCredits { get; private set; } = [];
    public IReadOnlyList<AboutCredit> ClubContributors { get; private set; } = [];
    public AsyncRelayCommand OpenPrivacyCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenOriginalSourceCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenCommunitySourceCommand { get; private set; } = null!;
    private void InitializeAbout()
    {
        AboutCredit Credit(string name, string description, string label = "", string? uri = null, string? portrait = null)
        {
            Bitmap? bitmap = null;
            if (portrait is not null)
            {
                try { using var file = AssetLoader.Open(new Uri("avares://SHOU.Launcher.Desktop/Assets/" + portrait)); bitmap = Bitmap.DecodeToWidth(file, 96); }
                catch (IOException) { }
            }
            return new(name, description, label, uri, bitmap, uri is null ? null : OpenLink("打开链接", uri));
        }
        OriginalCredits = [Credit("龙腾猫跃", "Plain Craft Launcher 的原作者", "赞助原作者", "https://ifdian.net/a/LTCat", "Credits/LTCat.jpg"),
            Credit("PCL Community", "Plain Craft Launcher Community Edition 的开发团队", "GitHub 主页", "https://github.com/PCL-Community", "Credits/PCL-Community.png"),
            Credit("潮涌核心社启动器", "由社团独立维护的跨平台新版本", "查看源代码", "https://github.com/Conduit-Club/SHOU-Launcher", "Conduit/club-logo.png")];
        ClubAcknowledgements = [Credit("特别鸣谢 · 社团指导老师-ZRY", "为 Minecraft 潮涌核心社团的创立提供了大力支持。", "了解指导老师", "https://xxxy.shou.edu.cn/_t2275/2023/0706/c17034a320081/page.psp"),
            Credit("特别贡献 · 空间博士-ZST", "为社团在虚拟现实与 Unity 技术上的发展作出了贡献。", "了解空间博士", "https://xxxy.shou.edu.cn/_t2275/2023/0707/c17036a353973/page.psp")];
        HistoricalCredits = [Credit("bangbang93 / BMCLAPI", "Minecraft 下载镜像服务", "访问项目", "https://bmclapidoc.bangbang93.com/"),
            Credit("MC 百科", "中文 Minecraft 模组资料", "打开 MC 百科", "https://www.mcmod.cn"),
            Credit("Pysio", "历史项目支持与贡献", "访问网站", "https://www.pysio.online"), Credit("Yunmoan", "历史项目支持与贡献", "访问网站", "https://www.zyghit.cn"),
            Credit("EasyTier", "历史联机组件贡献；新版本已移除联机功能", "访问项目", "https://easytier.cn"), Credit("Z0z0r4", "历史项目贡献者"), Credit("Emperormummy", "历史项目贡献者")];
        ClubContributors = [Credit("Moeary", "项目开发与社团维护"), Credit("Codex", "开发协作与测试")];
        OpenPrivacyCommand = OpenLink("打开隐私说明", "https://github.com/Conduit-Club/SHOU-Launcher/blob/main/docs/conduit/PRIVACY.md");
        OpenOriginalSourceCommand = OpenLink("打开原项目源代码", "https://github.com/Meloong-Git/PCL");
        OpenCommunitySourceCommand = OpenLink("打开 PCL-CE 源代码", "https://github.com/PCL-Community/PCL-CE");
    }
    private void DisposeAbout()
    { foreach (var row in OriginalCredits.Concat(ClubAcknowledgements).Concat(HistoricalCredits).Concat(ClubContributors)) row.Portrait?.Dispose(); }
}
