using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record ServerChoice(string Id, string Name, string Detail, string Status);

public sealed class HomePageViewModel : PageViewModel
{
    public ObservableCollection<ServerChoice> Servers { get; } = [];
    private ServerChoice? _selectedServer;
    public ServerChoice? SelectedServer
    {
        get => _selectedServer;
        set { if (SetProperty(ref _selectedServer, value)) { OnPropertyChanged(nameof(DestinationSummary)); Owner.NotifyState(); } }
    }
    public string SelectedServerId => SelectedServer?.Id ?? "none";
    public string DestinationSummary => SelectedServerId == "none" ? "只启动游戏，不自动连接服务器。" : $"启动后前往 {SelectedServer?.Name}。离线账户不能进入需认证的社团服。";
    public string BrandName => Owner.Snapshot.Build.Name;
    public string PlatformSummary => Owner.PlatformSummary;
    public string DataDirectory => Service.Paths.DataDirectory;
    public string CapabilitiesSummary => Service.Capabilities.Limitations.Count == 0 ? "平台基础能力已就绪。" : string.Join("\n", Service.Capabilities.Limitations);
    public AsyncRelayCommand RefreshServersCommand { get; }
    public AsyncRelayCommand OpenWikiCommand { get; }
    public AsyncRelayCommand OpenGuideCommand { get; }
    public AsyncRelayCommand OpenReleasesCommand { get; }
    public AsyncRelayCommand OpenDataDirectoryCommand { get; }

    public HomePageViewModel(MainWindowViewModel owner) : base(owner, "社团首页", "五湖四海之士，汇于东海之滨")
    {
        RefreshServersCommand = AsyncCommand(() => Owner.RunAsync("查询服务器", ct => Service.RefreshServersAsync(ct), true), () => Owner.CanReadRemote);
        OpenWikiCommand = Open("https://conduit-club.github.io/");
        OpenGuideCommand = Open("https://conduit-club.github.io/servers/velocity/");
        OpenReleasesCommand = Open("https://github.com/Conduit-Club/SHOU-Launcher/releases");
        OpenDataDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开数据目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(Service.Paths.DataDirectory, ct)));
    }
    private AsyncRelayCommand Open(string url) => AsyncCommand(() => Owner.RunControlAsync("打开链接", ct => Owner.ExternalLauncher.OpenUriAsync(new Uri(url), ct)));
    public override void Refresh(LauncherSnapshot snapshot)
    {
        var selectedId = SelectedServerId;
        var choices = new List<ServerChoice> { new("none", "只启动游戏", "不自动入服", "本地启动") };
        foreach (var server in snapshot.Servers.Where(s => s.Id != "none"))
            choices.Add(new(server.Id, server.Name, server.Address, server.OnlinePlayers is { } online ? $"{online} 人在线 · {server.Status}" : server.Status));
        if (!Servers.SequenceEqual(choices)) { Servers.Clear(); foreach (var choice in choices) Servers.Add(choice); }
        _selectedServer = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers[0];
        OnPropertyChanged(nameof(SelectedServer));
        OnPropertyChanged(nameof(DestinationSummary));
        OnPropertyChanged(nameof(BrandName));
        OnPropertyChanged(nameof(PlatformSummary));
        OnPropertyChanged(nameof(CapabilitiesSummary));
        base.Refresh(snapshot);
    }
}
