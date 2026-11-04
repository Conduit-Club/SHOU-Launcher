using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record LoginProviderChoice(string Id, string Name, Uri? AuthenticationServer = null);

public sealed class AccountsPageViewModel : PageViewModel
{
    private bool _refreshing;
    private AccountProfile? _selectedAccount;
    private LoginProviderChoice _provider;
    private string _playerName = "";
    private string _username = "";
    private string _password = "";
    private DeviceCodeChallenge? _challenge;
    private bool _confirmRemoval;
    public ObservableCollection<AccountProfile> Accounts { get; } = [];
    public IReadOnlyList<LoginProviderChoice> Providers { get; } =
    [
        new("offline", "离线角色"), new("microsoft", "微软正版"),
        new("mua", "MUA", new Uri("https://skin.mualliance.ltd/api/yggdrasil")),
        new("littleskin", "LittleSkin", new Uri("https://littleskin.cn/api/yggdrasil"))
    ];
    public LoginProviderChoice SelectedProvider
    {
        get => _provider;
        set
        {
            if (value is null || !SetProperty(ref _provider, value)) return;
            Password = "";
            OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsMicrosoft)); OnPropertyChanged(nameof(IsYggdrasil));
            NotifyCommands();
        }
    }
    public bool IsOffline => SelectedProvider.Id == "offline";
    public bool IsMicrosoft => SelectedProvider.Id == "microsoft";
    public bool IsYggdrasil => SelectedProvider.AuthenticationServer is not null;
    public string PlayerName { get => _playerName; set { SetProperty(ref _playerName, value); NotifyCommands(); } }
    public string Username { get => _username; set { SetProperty(ref _username, value); NotifyCommands(); } }
    public string Password { get => _password; set { SetProperty(ref _password, value); NotifyCommands(); } }
    public bool ConfirmRemoval { get => _confirmRemoval; set { SetProperty(ref _confirmRemoval, value); NotifyCommands(); } }
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!_refreshing && !Owner.CanMutate) { OnPropertyChanged(nameof(SelectedAccount)); return; }
            if (!SetProperty(ref _selectedAccount, value)) return;
            ConfirmRemoval = false;
            OnPropertyChanged(nameof(AccountDetail));
            NotifyCommands();
            if (!_refreshing && value is not null)
                _ = Owner.RunAsync("选择账户", ct => Service.SelectAccountAsync(value.Id, ct));
        }
    }
    public string AccountDetail => SelectedAccount is { } account
        ? $"{account.DisplayName} · {account.Kind}\n{UiPrivacy.Redact(account.Status)} · {(account.Persisted ? "已保存" : "仅本次会话")}\n{account.AuthenticationServer ?? "本地/微软认证"}"
        : "尚未选择账户。相同昵称的不同来源角色仍是独立账户。";
    public string PersistenceSummary => Owner.Snapshot.CanPersistAccounts && Service.Capabilities.SupportsCredentialPersistence
        ? "凭据由系统安全存储保存，账户列表不含密码或令牌。"
        : "当前无法安全持久保存账户；保留已有档案，新授权可能仅在本次会话有效。";
    public bool HasDeviceChallenge => _challenge is not null;
    public string DeviceCode => _challenge?.UserCode ?? "";
    public string DeviceInstructions => _challenge is { } challenge ? $"在微软官网输入此代码，有效至 {challenge.ExpiresAt.ToLocalTime():HH:mm:ss}。\n{UiPrivacy.Redact(challenge.Message)}" : "";
    public AsyncRelayCommand AddOfflineCommand { get; }
    public AsyncRelayCommand LoginMicrosoftCommand { get; }
    public AsyncRelayCommand LoginYggdrasilCommand { get; }
    public AsyncRelayCommand OpenVerificationCommand { get; }
    public AsyncRelayCommand RefreshAccountCommand { get; }
    public AsyncRelayCommand RemoveAccountCommand { get; }

    public AccountsPageViewModel(MainWindowViewModel owner) : base(owner, "账户", "管理离线、Microsoft、MUA 与 LittleSkin 角色。")
    {
        _provider = Providers[0];
        AddOfflineCommand = AsyncCommand(() => Owner.RunAsync("添加离线角色", async ct => { await Service.AddOfflineAccountAsync(PlayerName.Trim(), ct); }), () => CanEdit && !string.IsNullOrWhiteSpace(PlayerName));
        LoginMicrosoftCommand = AsyncCommand(LoginMicrosoftAsync, () => CanEdit);
        LoginYggdrasilCommand = AsyncCommand(LoginYggdrasilAsync, () => CanEdit && IsYggdrasil && !string.IsNullOrWhiteSpace(Username) && Password.Length > 0);
        OpenVerificationCommand = AsyncCommand(() => Owner.RunControlAsync("打开微软授权页", ct => Owner.ExternalLauncher.OpenUriAsync(_challenge!.VerificationUri, ct)), () => _challenge is { VerificationUri.Scheme: "https" });
        RefreshAccountCommand = AsyncCommand(() => Owner.RunAsync("刷新账户", ct => Service.RefreshAccountAsync(SelectedAccount!.Id, ct)), () => CanEdit && SelectedAccount is { Kind: not AccountKind.Offline });
        RemoveAccountCommand = AsyncCommand(async () =>
        {
            var id = SelectedAccount!.Id;
            await Owner.RunAsync("移除本机账户", ct => Service.RemoveAccountAsync(id, ct));
            ConfirmRemoval = false;
        }, () => CanEdit && SelectedAccount is not null && ConfirmRemoval);
    }
    private async Task LoginMicrosoftAsync()
    {
        SetChallenge(null);
        try
        {
            var progress = new DispatchProgress<DeviceCodeChallenge>(challenge => Owner.Dispatch(() => SetChallenge(challenge)));
            await Owner.RunAsync("微软设备授权", async ct => { await Service.LoginMicrosoftAsync(progress, ct); });
        }
        finally { SetChallenge(null); }
    }
    private async Task LoginYggdrasilAsync()
    {
        var server = SelectedProvider.AuthenticationServer!;
        var username = Username.Trim();
        var password = Password;
        Password = "";
        await Owner.RunAsync("登录 " + SelectedProvider.Name, async ct =>
        {
            var accounts = await Service.LoginYggdrasilAsync(server, username, password, ct);
            if (accounts.Count == 0) throw new InvalidOperationException("认证服务没有返回可用角色。");
        }, false, password);
    }
    private void SetChallenge(DeviceCodeChallenge? challenge)
    {
        _challenge = challenge;
        OnPropertyChanged(nameof(HasDeviceChallenge)); OnPropertyChanged(nameof(DeviceCode)); OnPropertyChanged(nameof(DeviceInstructions));
        OpenVerificationCommand.NotifyCanExecuteChanged();
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        _refreshing = true;
        try
        {
            if (!Accounts.SequenceEqual(snapshot.Accounts)) { Accounts.Clear(); foreach (var account in snapshot.Accounts) Accounts.Add(account); }
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == snapshot.SelectedAccountId);
            OnPropertyChanged(nameof(AccountDetail)); OnPropertyChanged(nameof(PersistenceSummary));
        }
        finally { _refreshing = false; }
        base.Refresh(snapshot);
    }
}

internal sealed class DispatchProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
