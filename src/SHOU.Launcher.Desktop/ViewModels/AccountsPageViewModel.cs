using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record LoginProviderChoice(string Id, string Name, Uri? AuthenticationServer = null);

public sealed partial class AccountsPageViewModel : PageViewModel
{
    private bool _refreshing;
    private AccountProfile? _selectedAccount;
    private LoginProviderChoice _provider;
    private string _playerName = "";
    private string _username = "";
    private string _password = "";
    private DeviceCodeChallenge? _challenge;
    private AuthenticationProfileChallengeViewModel? _profileChallenge;
    private long _authenticationGeneration;
    private bool _authenticationActive;
    private long _openedDeviceGeneration = -1;
    private string _deviceAutomationStatus = "";
    public Func<string, CancellationToken, Task>? DeviceCodeClipboardWriter { get; set; }
    public string DeviceAutomationStatus { get => _deviceAutomationStatus; private set => SetProperty(ref _deviceAutomationStatus, value); }
    public RelayCommand CancelMicrosoftAuthorizationCommand { get; private set; } = null!;
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
            if (value is null || (!_refreshing && !Owner.CanMutate) || !SetProperty(ref _provider, value)) return;
            Password = "";
            OnPropertyChanged(nameof(IsOffline)); OnPropertyChanged(nameof(IsMicrosoft)); OnPropertyChanged(nameof(ShowMicrosoftLoginForm)); OnPropertyChanged(nameof(IsYggdrasil));
            OnPropertyChanged(nameof(AddingAccountSummary));
            UpdateChannelAccounts();
            NotifyCommands();
        }
    }
    public bool IsOffline => SelectedProvider.Id == "offline";
    public bool IsMicrosoft => SelectedProvider.Id == "microsoft";
    public bool ShowMicrosoftLoginForm => IsMicrosoft && !HasDeviceChallenge;
    public bool IsYggdrasil => SelectedProvider.AuthenticationServer is not null;
    public string PlayerName { get => _playerName; set { SetProperty(ref _playerName, value ?? ""); OnPropertyChanged(nameof(HasNonAsciiPlayerName)); NotifyCommands(); } }
    public bool HasNonAsciiPlayerName => PlayerName.Any(c => c > 127);
    private OfflineUuidMode offlineUuidMode;
    private string customOfflineUuid = "";
    public bool IsStandardUuid { get => offlineUuidMode == OfflineUuidMode.Standard; set { if (value) SetUuidMode(OfflineUuidMode.Standard); } }
    public bool IsLegacyUuid { get => offlineUuidMode == OfflineUuidMode.Legacy; set { if (value) SetUuidMode(OfflineUuidMode.Legacy); } }
    public bool IsCustomUuid { get => offlineUuidMode == OfflineUuidMode.Custom; set { if (value) SetUuidMode(OfflineUuidMode.Custom); } }
    public string CustomOfflineUuid { get => customOfflineUuid; set { SetProperty(ref customOfflineUuid, value ?? ""); NotifyCommands(); } }
    public bool SupportsOfflineUuidModes => Service is IOfflineAccountService;
    private void SetUuidMode(OfflineUuidMode mode)
    {
        offlineUuidMode = mode;
        OnPropertyChanged(nameof(IsStandardUuid)); OnPropertyChanged(nameof(IsLegacyUuid)); OnPropertyChanged(nameof(IsCustomUuid)); NotifyCommands();
    }
    public string Username { get => _username; set { SetProperty(ref _username, value ?? ""); NotifyCommands(); } }
    public string Password { get => _password; set { SetProperty(ref _password, value ?? ""); NotifyCommands(); } }
    public bool ConfirmRemoval { get => _confirmRemoval; set { SetProperty(ref _confirmRemoval, value); NotifyCommands(); } }
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!_refreshing && !Owner.CanMutate) { OnPropertyChanged(nameof(SelectedAccount)); return; }
            if (!SetProperty(ref _selectedAccount, value)) return;
            ConfirmRemoval = false;
            if (!_refreshing && ShowAllAccounts && value is not null) SelectProviderForAccount(value);
            OnPropertyChanged(nameof(AccountDetail));
            NotifySelectedAccount();
            NotifyCommands();
            if (!_refreshing && value is not null)
                _ = Owner.RunAsync("选择账户", ct => Service.SelectAccountAsync(value.Id, ct));
        }
    }
    public string AccountDetail => SelectedAccount is { } account
        ? $"{account.DisplayName} · {account.Kind}\n{UiPrivacy.Redact(account.Status)} · {(account.Persisted ? "已保存" : "仅本次会话")}\n{UiPrivacy.Redact(account.AuthenticationServer ?? "本地/微软认证")}"
        : "尚未选择账户。相同昵称的不同来源角色仍是独立账户。";
    public string PersistenceSummary => Owner.Snapshot.CanPersistAccounts && Service.Capabilities.SupportsCredentialPersistence
        ? "凭据由系统安全存储保存，账户列表不含密码或令牌。"
        : "当前无法安全持久保存账户；保留已有档案，新授权可能仅在本次会话有效。";
    public bool HasDeviceChallenge => _challenge is not null;
    public string DeviceCode => _challenge?.UserCode ?? "";
    public string DeviceInstructions => _challenge is { } challenge ? $"在微软官网输入此代码，有效至 {challenge.ExpiresAt.ToLocalTime():HH:mm:ss}。\n{UiPrivacy.Redact(challenge.Message)}" : "";
    public AuthenticationProfileChallengeViewModel? ProfileChallenge
    {
        get => _profileChallenge;
        private set { if (SetProperty(ref _profileChallenge, value)) OnPropertyChanged(nameof(HasProfileChallenge)); }
    }
    public bool HasProfileChallenge => ProfileChallenge is { IsPending: true };
    public bool IsAuthenticating => _authenticationActive;
    public AsyncRelayCommand AddOfflineCommand { get; }
    public AsyncRelayCommand LoginMicrosoftCommand { get; }
    public AsyncRelayCommand LoginYggdrasilCommand { get; }
    public AsyncRelayCommand OpenVerificationCommand { get; }
    public AsyncRelayCommand RefreshAccountCommand { get; }
    public AsyncRelayCommand RemoveAccountCommand { get; }

    public AccountsPageViewModel(MainWindowViewModel owner) : base(owner, "账户", "管理离线、Microsoft、MUA 与 LittleSkin 角色。")
    {
        _provider = Providers.First(p => p.Id == "microsoft");
        AddOfflineCommand = AsyncCommand(async () =>
        {
            var name = PlayerName.Trim(); var mode = offlineUuidMode; var custom = CustomOfflineUuid.Trim();
            if (await Owner.RunAsync("添加离线角色", async ct =>
            {
                if (Service is IOfflineAccountService identities) await identities.AddOfflineAccountAsync(name, mode, custom, ct);
                else if (mode == OfflineUuidMode.Standard) await Service.AddOfflineAccountAsync(name, ct);
                else throw new InvalidOperationException("当前服务不支持此 UUID 标准。");
            })) IsAddingAccount = false;
        }, () => CanEdit && !string.IsNullOrWhiteSpace(PlayerName) && (!IsCustomUuid || (Guid.TryParseExact(CustomOfflineUuid.Trim(), "N", out var id) || Guid.TryParseExact(CustomOfflineUuid.Trim(), "D", out id)) && id != Guid.Empty));
        LoginMicrosoftCommand = AsyncCommand(LoginMicrosoftAsync, () => CanEdit);
        LoginYggdrasilCommand = AsyncCommand(LoginYggdrasilAsync, () => CanEdit && IsYggdrasil && !string.IsNullOrWhiteSpace(Username) && Password.Length > 0);
        OpenVerificationCommand = AsyncCommand(() => Owner.RunControlAsync("打开微软授权页", ct => Owner.ExternalLauncher.OpenUriAsync(_challenge!.VerificationUri, ct)), () => _challenge is { VerificationUri.Scheme: "https" });
        CancelMicrosoftAuthorizationCommand = Command(() => Owner.CancelOperationCommand.Execute(null));
        RefreshAccountCommand = AsyncCommand(() => Owner.RunAsync("刷新账户", ct => Service.RefreshAccountAsync(SelectedAccount!.Id, ct)), () => CanEdit && SelectedAccount is { Kind: not AccountKind.Offline });
        RemoveAccountCommand = AsyncCommand(async () =>
        {
            var id = SelectedAccount!.Id;
            await Owner.RunAsync("移除本机账户", ct => Service.RemoveAccountAsync(id, ct));
            ConfirmRemoval = false;
        }, () => CanEdit && SelectedAccount is not null && ConfirmRemoval);
        InitializeAccountPanel();
        InitializeAccountAppearance();
    }
    private async Task LoginMicrosoftAsync()
    {
        var generation = BeginAuthentication();
        try
        {
            if (await Owner.RunInlineAsync("微软设备授权", async ct =>
            {
                var progress = new DispatchProgress<DeviceCodeChallenge>(challenge => Owner.Dispatch(() =>
                {
                    if (IsCurrentAuthentication(generation) && !ct.IsCancellationRequested)
                    {
                        SetChallenge(challenge);
                        if (_openedDeviceGeneration != generation)
                        {
                            _openedDeviceGeneration = generation;
                            _ = PrepareDeviceAuthorizationAsync(challenge, generation, ct);
                        }
                    }
                }));
                await Service.LoginMicrosoftAsync(progress, ct);
            })) IsAddingAccount = false;
        }
        finally { EndAuthentication(generation); }
    }
    private async Task PrepareDeviceAuthorizationAsync(DeviceCodeChallenge challenge, long generation, CancellationToken ct)
    {
        var copied = false;
        try
        {
            if (!IsCurrentAuthentication(generation) || ct.IsCancellationRequested) return;
            if (DeviceCodeClipboardWriter is { } writer) { await writer(challenge.UserCode, ct); copied = true; }
        }
        catch (Exception) { /* Clipboard failure must not end an otherwise valid device authorization. */ }
        try
        {
            if (!IsCurrentAuthentication(generation) || ct.IsCancellationRequested) return;
            await Owner.ExternalLauncher.OpenUriAsync(challenge.VerificationUri, ct);
            if (IsCurrentAuthentication(generation) && !ct.IsCancellationRequested)
                DeviceAutomationStatus = copied ? "代码已复制，已打开默认浏览器；粘贴代码后完成授权。" : "已打开默认浏览器；请复制上方代码完成授权。";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsCurrentAuthentication(generation) && !ct.IsCancellationRequested)
                DeviceAutomationStatus = copied ? "代码已复制；浏览器未能打开，请点击“打开微软授权页”重试。" : "请点击下方按钮打开授权页并复制代码。";
        }
    }
    private async Task LoginYggdrasilAsync()
    {
        var server = SelectedProvider.AuthenticationServer!;
        var username = Username.Trim();
        var password = Password;
        Password = "";
        var generation = BeginAuthentication();
        try
        {
            if (await Owner.RunAsync("登录 " + SelectedProvider.Name, async ct =>
            {
                var accounts = await Service.LoginYggdrasilWithProfileSelectionAsync(server, username, password,
                    (choices, selectionToken) => SelectProfileAsync(choices, generation, ct, selectionToken), ct);
                if (accounts.Count == 0) throw new InvalidOperationException("认证服务没有返回可用角色。");
            }, false, password)) IsAddingAccount = false;
        }
        finally { EndAuthentication(generation); }
    }
    private long BeginAuthentication()
    {
        IsAddingAccount = true;
        var generation = ++_authenticationGeneration;
        _authenticationActive = true;
        ClearProfileChallenge(new CancellationToken(true));
        SetChallenge(null);
        OnPropertyChanged(nameof(IsAuthenticating));
        return generation;
    }
    private bool IsCurrentAuthentication(long generation) => _authenticationActive && generation == _authenticationGeneration;
    private void EndAuthentication(long generation)
    {
        if (!IsCurrentAuthentication(generation)) return;
        _authenticationActive = false;
        ClearProfileChallenge(new CancellationToken(true));
        SetChallenge(null);
        OnPropertyChanged(nameof(IsAuthenticating));
    }
    private async Task<string?> SelectProfileAsync(IReadOnlyList<AuthenticationProfileChoice> choices, long generation,
        CancellationToken authenticationToken, CancellationToken selectionToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(authenticationToken, selectionToken);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        if (choices.Count == 0) throw new InvalidOperationException("认证服务没有提供可选择的角色。");
        AuthenticationProfileChallengeViewModel challenge = null!;
        challenge = new(choices,
            () => IsCurrentAuthentication(generation) && ReferenceEquals(ProfileChallenge, challenge) && !token.IsCancellationRequested,
            () => Owner.CancelOperationCommand.Execute(null));
        using var registration = token.Register(() =>
        {
            challenge.Cancel(token);
            Owner.Dispatch(() => ClearProfileChallenge(token, challenge));
        });
        Owner.Dispatch(() =>
        {
            if (!IsCurrentAuthentication(generation) || token.IsCancellationRequested) { challenge.Cancel(token); return; }
            ClearProfileChallenge(new CancellationToken(true));
            ProfileChallenge = challenge;
        });
        try { return await challenge.Completion.ConfigureAwait(false); }
        finally { Owner.Dispatch(() => ClearProfileChallenge(token, challenge)); }
    }
    private void ClearProfileChallenge(CancellationToken token, AuthenticationProfileChallengeViewModel? expected = null)
    {
        if (ProfileChallenge is not { } current || (expected is not null && !ReferenceEquals(current, expected))) return;
        current.Cancel(token);
        current.NotifyCompleted();
        ProfileChallenge = null;
    }
    internal void OnNavigatingAway()
    {
        CancelAccountAppearanceWork();
        Password = "";
        if (_authenticationActive) Owner.CancelOperationCommand.Execute(null);
    }
    internal void CancelAuthenticationState(CancellationToken token)
    {
        CancelAccountAppearanceWork();
        if (!_authenticationActive && ProfileChallenge is null && _challenge is null) return;
        ++_authenticationGeneration;
        _authenticationActive = false;
        ClearProfileChallenge(token);
        SetChallenge(null);
        Password = "";
        OnPropertyChanged(nameof(IsAuthenticating));
    }
    private void SetChallenge(DeviceCodeChallenge? challenge)
    {
        if (_challenge == challenge) return;
        _challenge = challenge;
        DeviceAutomationStatus = "";
        OnPropertyChanged(nameof(HasDeviceChallenge)); OnPropertyChanged(nameof(ShowMicrosoftLoginForm)); OnPropertyChanged(nameof(DeviceCode)); OnPropertyChanged(nameof(DeviceInstructions));
        OpenVerificationCommand.NotifyCanExecuteChanged();
    }
    public override void Refresh(LauncherSnapshot snapshot)
    {
        _refreshing = true;
        try
        {
            if (!Accounts.SequenceEqual(snapshot.Accounts)) { Accounts.Clear(); foreach (var account in snapshot.Accounts) Accounts.Add(account); }
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == snapshot.SelectedAccountId);
            RefreshAccountPanel();
            RefreshAccountAppearanceRows();
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
