using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record ServerChoice(string Id, string Name, string Detail, string Status);

public sealed class HomePageViewModel : PageViewModel
{
    private bool _showClubIntroduction;
    private bool _showClubServers;
    private bool _rotateHomeBackground = true;
    private double _windowOpacity = 1;
    public bool ShowClubIntroduction => _showClubIntroduction;
    public bool ShowClubServers => _showClubServers;
    public bool RotateHomeBackground => _rotateHomeBackground;
    public double WindowOpacity => _windowOpacity;
    public ObservableCollection<ServerChoice> Servers { get; } = [];
    private ServerChoice? _selectedServer;
    public ServerChoice? SelectedServer
    {
        get => _selectedServer;
        set { if (SetProperty(ref _selectedServer, value)) { OnPropertyChanged(nameof(DestinationSummary)); Owner.NotifyState(); } }
    }
    public string SelectedServerId => SelectedServer?.Id ?? "none";
    public string DestinationSummary => SelectedServerId == "none" ? "只启动游戏，不自动连接服务器。" : $"可查看 {SelectedServer?.Name} 的状态；自动入服暂不可用，请选择“只启动游戏”。";
    public string BrandName => Owner.Snapshot.Build.Name;
    public string ClubName => "潮涌核心社";
    public string SelectedInstanceName => Owner.Snapshot.Instances.FirstOrDefault(i => i.Id == Owner.Snapshot.SelectedInstanceId)?.Name ?? "尚未选择实例";
    public string OnlinePlayerSummary
    {
        get
        {
            var servers = Owner.Snapshot.Servers.Where(s => s.Id != "none").ToArray();
            return servers.Length > 0 && servers.All(s => s.OnlinePlayers is not null)
                ? servers.Sum(s => s.OnlinePlayers ?? 0).ToString() : "未知";
        }
    }
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
    public void SetPresentation(bool showIntroduction, bool showServers, bool rotateBackground = true, double opacity = 1)
    {
        SetProperty(ref _showClubIntroduction, showIntroduction, nameof(ShowClubIntroduction));
        SetProperty(ref _showClubServers, showServers, nameof(ShowClubServers));
        SetProperty(ref _rotateHomeBackground, rotateBackground, nameof(RotateHomeBackground));
        SetProperty(ref _windowOpacity, Math.Clamp(double.IsFinite(opacity) ? opacity : 1, 0.25, 1), nameof(WindowOpacity));
    }
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
        OnPropertyChanged(nameof(SelectedInstanceName));
        OnPropertyChanged(nameof(OnlinePlayerSummary));
        OnPropertyChanged(nameof(PlatformSummary));
        OnPropertyChanged(nameof(CapabilitiesSummary));
        base.Refresh(snapshot);
    }
}
