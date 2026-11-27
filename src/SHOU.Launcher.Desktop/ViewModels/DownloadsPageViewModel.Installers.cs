using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record InstallerVersionRow(InstallerVersion Version)
{
    public string Title => Version.Loader == "optifine" ? Version.GameVersion + " " + Version.Version : Version.Version;
    public string FileName => SHOU.Launcher.Core.Loaders.InstallerCatalogReader.InstallerFileName(Version);
    public string Detail => string.Join(" · ", new[] { Version.Recommended ? "推荐版" : null, Version.Latest ? "最新版本" : null,
        Version.Channel is "production" or "stable" ? "稳定版" : Version.Channel is "snapshot" or "preview" ? "测试版" : null,
        Version.Published is { } date ? $"发布于 {date.LocalDateTime:yyyy/M/d HH:mm}" : null }.Where(x => x is not null));
    public string Verification => Version.Loader is "labymod" or "liteloader" ? "下载并另存安装器；官方 HTTPS、大小与 JAR 结构检查。上游未提供此安装器的文件哈希。" : "下载并另存安装器；验证来源、大小及文件哈希。";
    public Avalonia.Media.IImage Icon => LoaderArtwork.For(Version.Loader);
}
public sealed record InstallerVersionGroup(string Header, IReadOnlyList<InstallerVersionRow> Versions, bool IsExpanded)
{
    public IReadOnlyList<InstallerVersionRow> Highlights => Versions.Count <= 2 ? [] : Versions.Where(v => v.Version.Recommended || v.Version.Latest).OrderByDescending(v => v.Version.Recommended).ThenByDescending(v => v.Version.Latest).ToArray();
    public bool HasHighlights => Highlights.Count > 0;
    public string AllVersionsHeader => $"全部版本 ({Versions.Count})";
}

public sealed partial class DownloadsPageViewModel
{
    private string? _loadedInstaller;
    private string _installerStatus = "";
    public ObservableCollection<InstallerVersionGroup> InstallerVersionGroups { get; } = [];
    public bool HasInstallerVersions => InstallerVersionGroups.Count > 0;
    public string InstallerStatus { get => _installerStatus; private set => SetProperty(ref _installerStatus, value); }
    public AsyncRelayCommand RefreshInstallerCatalogCommand { get; private set; } = null!;
    public string InstallerIntroduction => SelectedCategory.Id switch
    {
        "installer-forge" => "Forge 是一个模组加载器；安装对应版本后，即可使用为 Forge 开发的模组。",
        "installer-neoforge" => "NeoForge 是一个模组加载器；请选择与 Minecraft 版本对应的安装器。",
        "installer-fabric" or "installer-legacyfabric" => "Fabric 安装器可选择游戏与加载器版本。这里下载独立安装器文件。",
        "installer-quilt" => "Quilt 安装器可选择游戏与加载器版本。这里下载独立安装器文件。",
        "installer-optifine" => "OptiFine 可改善画面与性能，支持光影和高清材质；请留意与模组的兼容性。",
        "installer-cleanroom" => "Cleanroom 是基于 Forge 的 Minecraft 1.12.2 模组加载器。",
        "installer-labymod" => "LabyMod 是 Minecraft 的优化客户端。这里下载 Java 安装器，保存后可自行安装。",
        "installer-liteloader" => "LiteLoader 是适用于较早 Minecraft 版本的轻量模组加载器。",
        _ => "选择所需版本，或前往项目官方网站。"
    };
    private void InitializeInstallerBrowsing() => RefreshInstallerCatalogCommand = AsyncCommand(RefreshInstallerCatalogAsync, () => CanQuery && IsInstallerPage);
    private void StartInstallerCatalog()
    {
        if (IsInstallerPage && CanQuery && _loadedInstaller != SelectedCategory.Id) _ = RefreshInstallerCatalogAsync();
    }
    private async Task RefreshInstallerCatalogAsync()
    {
        if (!CanQuery || !IsInstallerPage) return;
        var category = SelectedCategory.Id; var revision = _requestRevision;
        if (_loadedInstaller != category) InstallerVersionGroups.Clear();
        InstallerStatus = ""; OnPropertyChanged(nameof(HasInstallerVersions));
        await ReadBrowseAsync("正在获取 " + InstallerTitle + " 版本列表", revision, async ct =>
        {
            if (Service is not IInstallerCatalogService service) { InstallerStatus = "当前服务未提供安装器目录，可前往官方网站。"; _loadedInstaller = category; return; }
            var result = await service.GetInstallerCatalogAsync(category[10..], ct); ct.ThrowIfCancellationRequested();
            if (revision != _requestRevision || SelectedCategory.Id != category) return;
            InstallerVersionGroups.Clear();
            foreach (var group in result.Versions.GroupBy(v => v.Loader == "optifine" && v.Channel == "preview" ? "快照版本" : v.GameVersion))
            {
                var versions = group.Select(v => new InstallerVersionRow(v)).ToArray();
                if (versions.Length <= 2) versions = versions.OrderByDescending(v => v.Version.Recommended).ToArray();
                InstallerVersionGroups.Add(new(group.Key, versions, InstallerVersionGroups.Count == 0));
            }
            _loadedInstaller = category;
            InstallerStatus = result.Notice ?? (result.Versions.Count == 0 ? "此来源没有返回可用安装器。" : "");
            OnPropertyChanged(nameof(HasInstallerVersions)); NotifyBrowseState();
        }, failure: message => InstallerStatus = message);
    }
    public async Task SaveInstallerAsync(InstallerVersionRow row, string destination)
    {
        if (!CanEdit || Service is not IInstallerCatalogService service) return;
        var success = await Owner.RunAsync("下载并校验安装器", ct => service.SaveInstallerAsync(row.Version, destination, ct));
        InstallerStatus = success ? "安装器已保存。" : Owner.ErrorMessage ?? "下载已取消，可重试。";
    }
}
