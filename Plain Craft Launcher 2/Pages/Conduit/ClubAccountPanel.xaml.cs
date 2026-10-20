using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Conduit;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL;

public partial class ClubAccountPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _snapshot;
    private string? _lastCurrent;
    private volatile bool _otherBusy;
    private string _editingProvider = "";
    private string _filter = "microsoft";
    private bool _defaultLoginPageRequested;

    public ClubAccountPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshAccounts();
            _timer.Start();
            RequestDefaultMicrosoftLoginPage();
        };
        Unloaded += (_, _) => { _timer.Stop(); };
        _timer.Tick += (_, _) => RefreshAccounts();
    }
    private void RefreshAccounts()
    {
        MicrosoftStatus.Text = ClubCatalog.MicrosoftApiReady
            ? MicrosoftClientConfigured ? "微软已配置" : "微软未配置"
            : "微软登录尚未开通，请先使用 LittleSkin 或 MUA。";
        LoginStatus.Text = _otherBusy ? "正在选择或探测认证服务，请稍候。" : AuthenticationBusy ? "正在验证账户；请先在登录窗口完成或取消授权。"
            : ProfileService.IsCreatingProfile && _editingProvider != "" ? $"正在添加：{_editingProvider}。可直接点击其他登录方式切换。"
            : "添加账户不会移除已保存的其他账户。";
        ProfileService.Load();
        var profiles = ProfileService.Profiles.ToArray();
        var current = ProfileService.Current?.ProfileId;
        if (current?.ToString() != _lastCurrent && !ProfileService.IsCreatingProfile && ProfileService.Current is { } active)
            _filter = Provider(active);
        _lastCurrent = current?.ToString();
        var snapshot = string.Join("|", profiles.Select(p => $"{p.ProfileId}:{p.UserName}:{p.Server}:{p.ServerName}:{p.ProfileType}:{ClubCatalog.AccountStatus(p)}")) + current;
        snapshot += _filter + ProfileService.IsCreatingProfile;
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        MuaButton.Content = $"MUA ({profiles.Count(p => Provider(p) == "mua")})";
        LittleButton.Content = $"LittleSkin ({profiles.Count(p => Provider(p) == "littleskin")})";
        MicrosoftButton.Content = $"微软(正版) ({profiles.Count(p => Provider(p) == "microsoft")})";
        var filtered = profiles.Where(p => Provider(p) == _filter).ToArray();
        Accounts.ItemsSource = filtered.Select(p => new { Profile = p, Title = $"{p.UserName}（{ClubCatalog.AccountSource(p)}）" + (p.ProfileId == current ? " ✓" : ""), Info = ClubCatalog.AccountStatus(p) }).ToArray();
        AccountSummary.Text = _filter switch { "mua" => "MUA Union", "littleskin" => "LittleSkin", "microsoft" => "微软正版", _ => "其他账户" };
        EmptyHint.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string Provider(McProfile p) => p.ProfileType == ProfileType.Microsoft ? "microsoft"
        : ClubCatalog.IsMuaProvider(p.Server) ? "mua"
        : ClubCatalog.IsProvider(p.Server, ClubCatalog.LittleSkinAuth) ? "littleskin" : "other";
    private void Provider_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeAccount()) return;
        _filter = ((FrameworkElement)sender).Tag?.ToString() ?? "other";
        var profiles = ProfileService.Profiles.Where(p => Provider(p) == _filter).ToArray();
        if (profiles.Length > 0) SelectAccount(profiles.FirstOrDefault(p => p.ProfileId == ProfileService.Current?.ProfileId) ?? profiles[0]);
        else OpenProvider(_filter);
        RefreshAccounts();
    }

    private void Account_Click(object sender, MouseButtonEventArgs e)
    {
        if (!CanChangeAccount()) return;
        SelectAccount((McProfile)((MyListItem)sender).Tag);
    }
    private void SelectAccount(McProfile profile)
    {
        ClearEditing();
        ProfileService.Select(profile);
        ModLaunch.mcLoginMsLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginAuthLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginLegacyLoader.State = ModBase.LoadState.Waiting;
        ModMain.frmLaunchLeft.RefreshPage(false);
        RefreshAccounts();
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeAccount()) return;
        OpenProvider(_filter);
    }
    private void OpenProvider(string provider)
    {
        if (provider == "microsoft" && ClubCatalog.MicrosoftApiReady && !MicrosoftClientConfigured && !ConfigureMicrosoft()) return;
        ClearEditing();
        if (provider == "other")
        {
            ModMain.frmLaunchLeft.RefreshPage(false);
            _otherBusy = true;
            ModBase.RunInNewThread(() =>
            {
                try { ProfileUi.CreateProfile(); }
                finally { ModBase.RunInUi(() => { _otherBusy = false; RefreshAccounts(); }); }
            });
            return;
        }
        _editingProvider = provider == "microsoft" ? "微软正版" : provider == "mua" ? "MUA Union" : "LittleSkin";
        ProfileService.IsCreatingProfile = true;
        if (provider != "microsoft")
        {
            PageLoginAuth.draggedAuthServer = provider == "mua" ? ClubCatalog.MuaAuth : ClubCatalog.LittleSkinAuth;
            PageLoginAuth.draggedAuthServerOAuthSupported = null;
        }
        ModMain.frmLaunchLeft.RefreshPage(false, provider == "microsoft" ? ModLaunch.McLoginType.Ms : ModLaunch.McLoginType.Auth);
        RefreshAccounts();
    }
    private static bool AuthenticationBusy => ModMain.frmLoginMs?.IsAuthenticating == true
        || ModMain.frmLoginAuth?.IsAuthenticating == true
        || ModLaunch.mcLoginMsLoader.State == ModBase.LoadState.Loading
        || ModLaunch.mcLoginAuthLoader.State == ModBase.LoadState.Loading;

    private bool CanChangeAccount()
    {
        if (_otherBusy) { HintService.Hint("正在选择或探测认证服务，请稍候。"); return false; }
        if (ModLaunch.isLaunching) { HintService.Hint("游戏正在启动，请稍后切换账户。"); return false; }
        if (AuthenticationBusy) { HintService.Hint("正在验证账户，请先在登录窗口完成或取消授权。"); return false; }
        return true;
    }

    private void ClearEditing()
    {
        ModMain.frmLoginAuth?.ClearEditing();
        ProfileService.IsCreatingProfile = false;
        _editingProvider = "";
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeAccount()) return;
        var profile = (McProfile)((FrameworkElement)sender).Tag;
        if (ModMain.MyMsgBox($"从本机移除 {profile.UserName}（{ClubCatalog.AccountSource(profile)}）？之后可以重新登录添加。",
                "移除本机账户", "移除", "取消") != 1) return;
        ClearEditing();
        ProfileService.Remove(profile);
        ModLaunch.mcLoginMsLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginAuthLoader.State = ModBase.LoadState.Waiting;
        ModLaunch.mcLoginLegacyLoader.State = ModBase.LoadState.Waiting;
        ModMain.frmLaunchLeft.RefreshPage(false);
        RefreshAccounts();
    }
    private static bool MicrosoftClientConfigured => !string.IsNullOrWhiteSpace(Config.System.ClubMicrosoftClientId) || !string.IsNullOrWhiteSpace(Secrets.MSOAuthClientId);
    private void MicrosoftConfig_Click(object sender, MouseButtonEventArgs e)
    {
        if (!CanChangeAccount()) return;
        if (!ClubCatalog.MicrosoftApiReady)
        {
            ShowMicrosoftUnavailable();
            return;
        }
        ConfigureMicrosoft();
    }

    private static void ShowMicrosoftUnavailable()
        => HintService.Hint("社团 Microsoft 登录尚未开通，API 申请与配置仍在处理中，请先使用 LittleSkin 或 MUA。", HintType.Warning);

    private void RequestDefaultMicrosoftLoginPage()
    {
        if (_defaultLoginPageRequested || ProfileService.Current is not null || ProfileService.IsCreatingProfile)
            return;
        _defaultLoginPageRequested = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ProfileService.Current is null && !ProfileService.IsCreatingProfile && ModMain.frmLaunchLeft is not null)
                ModMain.frmLaunchLeft.RefreshPage(false, ModLaunch.McLoginType.Ms);
        }));
    }
    private bool ConfigureMicrosoft()
    {
        var id = ModMain.MyMsgBoxInput("配置社团 Microsoft OAuth", "请输入社团应用的 Client ID（公开标识，不是 Client Secret）。更换 ID 后可能需要重新登录。尚未申请时可取消并使用 MUA Union 或 LittleSkin。", Config.System.ClubMicrosoftClientId);
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!Guid.TryParse(id.Trim(), out _)) { HintService.Hint("Client ID 应为有效的 UUID。"); return false; }
        Config.System.ClubMicrosoftClientId = id.Trim();
        ModLaunch.mcLoginMsLoader.State = ModBase.LoadState.Waiting;
        RefreshAccounts();
        return true;
    }
}
