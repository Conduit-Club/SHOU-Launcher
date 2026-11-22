using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class AccountsPageViewModel
{
    private bool _panelReady;
    private bool _providerInitialized;
    private bool _providerChosenByUser;
    private bool _initialAccountApplied;
    private bool _showAllAccounts;
    private bool _isAddingAccount;
    private bool _showRemoval;
    public ObservableCollection<AccountProfile> ChannelAccounts { get; } = [];
    public bool ShowAllAccounts
    {
        get => _showAllAccounts;
        private set { if (SetProperty(ref _showAllAccounts, value)) { OnPropertyChanged(nameof(AllAccountsButtonLabel)); NotifyPanelVisibility(); } }
    }
    public bool IsAddingAccount
    {
        get => _isAddingAccount;
        private set { if (SetProperty(ref _isAddingAccount, value)) { OnPropertyChanged(nameof(AddingAccountSummary)); NotifyPanelVisibility(); } }
    }
    public bool ShowRemoval { get => _showRemoval; private set => SetProperty(ref _showRemoval, value); }
    public bool ShowChannelAccount => !ShowAllAccounts;
    public bool ShowSelectedAccount => !ShowAllAccounts && !IsAddingAccount && SelectedChannelAccount is not null;
    public bool ShowEmptyChannel => !ShowAllAccounts && !IsAddingAccount && SelectedChannelAccount is null;
    public bool IsOfflineChannel => SelectedProvider.Id == "offline";
    public bool IsMicrosoftChannel => SelectedProvider.Id == "microsoft";
    public bool IsLittleSkinChannel => SelectedProvider.Id == "littleskin";
    public bool IsMuaChannel => SelectedProvider.Id == "mua";
    public string MicrosoftChannelTitle => Localization.UiLocalization.Instance.Format("微软正版登录（{0}）", Accounts.Count(a => a.Kind == AccountKind.Microsoft));
    public string LittleSkinChannelTitle => Localization.UiLocalization.Instance.Format("LittleSkin 登录（{0}）", CountProvider("littleskin"));
    public string MuaChannelTitle => Localization.UiLocalization.Instance.Format("MUA 登录（{0}）", CountProvider("mua"));
    public string OfflineChannelTitle => Localization.UiLocalization.Instance.Format("离线登录（{0}）", Accounts.Count(a => a.Kind == AccountKind.Offline));
    public string ChannelTitle => ShowAllAccounts ? Localization.UiLocalization.Instance.Format("全部账户（{0} 个角色）", Accounts.Count) : Localization.UiLocalization.Instance.Format("{0}（{1} 个角色）", Localization.UiLocalization.Instance.Text(SelectedProvider.Name), ChannelAccounts.Count);
    public string AllAccountsButtonLabel => ShowAllAccounts ? "隐藏" : "全部";
    public string AddingAccountSummary => Localization.UiLocalization.Instance.Format("正在添加：{0}。可直接点击其他登录方式切换。", Localization.UiLocalization.Instance.Text(SelectedProvider.Name));
    public string SelectedPlayerName => SelectedChannelAccount?.DisplayName ?? "尚未选择角色";
    public string SelectedPlayerSource => SelectedChannelAccount is { } account ? SourceName(account) : "";
    public string SelectedPlayerStatus => SelectedChannelAccount is { } account ? UiPrivacy.Redact(account.Status) + (account.Persisted ? " · 已保存" : " · 本次会话") : "";
    public AccountProfile? SelectedChannelAccount
    {
        get => ChannelAccounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);
        set { if (value is not null && ChannelAccounts.Any(a => a.Id == value.Id)) SelectedAccount = value; }
    }
    public RelayCommand<string> SelectProviderCommand { get; private set; } = null!;
    public RelayCommand ToggleAllAccountsCommand { get; private set; } = null!;
    public RelayCommand BeginAddAccountCommand { get; private set; } = null!;
    public RelayCommand EndAddAccountCommand { get; private set; } = null!;
    public RelayCommand RequestRemovalCommand { get; private set; } = null!;
    public RelayCommand CancelRemovalCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenProviderWebsiteCommand { get; private set; } = null!;

    private void InitializeAccountPanel()
    {
        SelectProviderCommand = new(id =>
        {
            if (!CanEdit) return;
            if (Providers.FirstOrDefault(p => p.Id == id) is { } provider) { _providerChosenByUser = true; SelectedProvider = provider; ShowAllAccounts = false; IsAddingAccount = ChannelAccounts.Count == 0; }
        }, _ => CanEdit);
        ToggleAllAccountsCommand = Command(() => { ShowAllAccounts = !ShowAllAccounts; IsAddingAccount = false; Password = ""; OnPropertyChanged(nameof(ChannelTitle)); }, () => CanEdit);
        BeginAddAccountCommand = Command(() => { ShowAllAccounts = false; IsAddingAccount = true; ShowRemoval = false; ConfirmRemoval = false; }, () => CanEdit);
        EndAddAccountCommand = Command(() => { IsAddingAccount = false; Password = ""; }, () => CanEdit);
        RequestRemovalCommand = Command(() => { ShowRemoval = true; ConfirmRemoval = false; }, () => CanEdit && SelectedChannelAccount is not null);
        CancelRemovalCommand = Command(() => { ShowRemoval = false; ConfirmRemoval = false; });
        OpenProviderWebsiteCommand = AsyncCommand(() => Owner.RunControlAsync("打开登录渠道官网", ct => Owner.ExternalLauncher.OpenUriAsync(new Uri(SelectedProvider.Id switch
        { "mua" => "https://skin.mualliance.ltd/", "littleskin" => "https://littleskin.cn/", _ => throw new InvalidOperationException("此渠道没有皮肤站入口。") }), ct)), () => IsYggdrasil);
        _panelReady = true;
    }

    private int CountProvider(string id) => Accounts.Count(a => ProviderId(a) == id);
    private void SelectProviderForAccount(AccountProfile account)
    {
        if (Providers.FirstOrDefault(p => p.Id == ProviderId(account)) is { } provider) SelectedProvider = provider;
    }
    private static string? ProviderId(AccountProfile account)
    {
        if (account.Kind == AccountKind.Offline) return "offline";
        if (account.Kind == AccountKind.Microsoft) return "microsoft";
        if (!Uri.TryCreate(account.AuthenticationServer, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        var endpoint = uri.AbsoluteUri.TrimEnd('/');
        if (endpoint.Equals("https://skin.mualliance.ltd/api/yggdrasil", StringComparison.OrdinalIgnoreCase)) return "mua";
        if (endpoint.Equals("https://littleskin.cn/api/yggdrasil", StringComparison.OrdinalIgnoreCase)) return "littleskin";
        return null;
    }
    public static string SourceName(AccountProfile account) => ProviderId(account) switch
    { "offline" => "离线角色", "microsoft" => "微软正版", "mua" => "MUA Union", "littleskin" => "LittleSkin", _ => "其他认证来源" };
    private void RefreshAccountPanel()
    {
        if (!_panelReady) return;
        if (!_providerInitialized)
        {
            if (SelectedAccount is { } selected && Providers.FirstOrDefault(p => p.Id == ProviderId(selected)) is { } provider) _provider = provider;
            _providerInitialized = true;
            OnPropertyChanged(nameof(SelectedProvider)); OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsMicrosoft)); OnPropertyChanged(nameof(IsYggdrasil));
            UpdateChannelAccounts();
            IsAddingAccount = ChannelAccounts.Count == 0;
        }
        // Configuration can arrive after the first empty snapshot. Preserve an explicit channel choice.
        if (!_providerChosenByUser && !_initialAccountApplied && SelectedAccount is { } initial)
        {
            SelectProviderForAccount(initial); _initialAccountApplied = true; IsAddingAccount = false;
        }
        UpdateChannelAccounts();
        foreach (var property in new[] { nameof(MicrosoftChannelTitle), nameof(LittleSkinChannelTitle), nameof(MuaChannelTitle), nameof(OfflineChannelTitle), nameof(ChannelTitle) }) OnPropertyChanged(property);
    }
    private void UpdateChannelAccounts()
    {
        if (!_panelReady) return;
        var accounts = Accounts.Where(a => ProviderId(a) == SelectedProvider.Id).ToArray();
        if (!ChannelAccounts.SequenceEqual(accounts)) { ChannelAccounts.Clear(); foreach (var account in accounts) ChannelAccounts.Add(account); }
        foreach (var property in new[] { nameof(IsOfflineChannel), nameof(IsMicrosoftChannel), nameof(IsLittleSkinChannel), nameof(IsMuaChannel), nameof(ChannelTitle) }) OnPropertyChanged(property);
        NotifySelectedAccount();
    }
    private void NotifySelectedAccount()
    {
        if (!_panelReady) return;
        OnPropertyChanged(nameof(SelectedChannelAccount)); OnPropertyChanged(nameof(SelectedPlayerName)); OnPropertyChanged(nameof(SelectedPlayerSource)); OnPropertyChanged(nameof(SelectedPlayerStatus));
        ShowRemoval = false; NotifyPanelVisibility();
        RefreshAccountAppearanceIdentity();
    }
    private void NotifyPanelVisibility()
    {
        OnPropertyChanged(nameof(ShowChannelAccount)); OnPropertyChanged(nameof(ShowSelectedAccount)); OnPropertyChanged(nameof(ShowEmptyChannel));
        RequestRemovalCommand?.NotifyCanExecuteChanged();
    }
    public override void NotifyCommands()
    {
        base.NotifyCommands();
        SelectProviderCommand?.NotifyCanExecuteChanged();
        NotifyAccountAppearanceCommands();
    }
}
