using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Yggdrasil;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Validate;

namespace PCL;

public partial class PageLoginAuth
{
    public static string draggedAuthServer;
    public static bool? draggedAuthServerOAuthSupported;

    // 预设服务器
    internal const string DefaultAuthServer = "https://littleskin.cn/api/yggdrasil";

    internal static readonly IReadOnlyDictionary<string, string> PredefinedAuthServers = new Dictionary<string, string>
    {
        { "MUA Union", PCL.Core.Conduit.ClubCatalog.MuaAuth },
        { Lang.Text("Launch.Account.Auth.Preset.LittleSkin"), DefaultAuthServer },
        { Lang.Text("Common.Option.Customize"), "" }
    };

    private bool _isRegisterMode = true;
    private bool _isOAuthMode;
    private bool? _oauthSupported;
    private bool _hasRegisterLink;
    private bool _loginModeInitialized;
    private int _authServerGeneration;
    private string _authServerUrl = "";
    public bool IsAuthenticating { get; private set; }

    public void ClearEditing()
    {
        if (IsAuthenticating) return;
        _authServerUrl = "";
        _authServerGeneration++;
        draggedAuthServer = null;
        draggedAuthServerOAuthSupported = null;
        TextName.Text = "";
        TextPass.Password = "";
    }

    public PageLoginAuth()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Loaded += (_, _) => ReloadRegisterButton();
        // Handles
        BtnBack.Click += BtnBack_Click;
        BtnBackOAuth.Click += BtnBack_Click;
        BtnLogin.Click += BtnLogin_Click;
        BtnOAuth.Click += BtnLogin_Click;
        BtnUsePassword.Click += (_, _) => _SetLoginMode(false);
        BtnUseOAuth.Click += (_, _) => _SetLoginMode(true);
        BtnWebsite.Click += BtnWebsite_Click;
        BtnPasswordWebsite.Click += BtnWebsite_Click;
        BtnLink.Click += Btn_Click;
    }

    public void Reload()
    {
        // Loaded can follow an explicit refresh. Do not consume the pending server twice.
        if (draggedAuthServer is null || IsAuthenticating) return;
        if (!string.Equals(_authServerUrl, draggedAuthServer, StringComparison.OrdinalIgnoreCase))
        {
            TextName.Text = "";
            TextPass.Password = "";
        }
        _authServerUrl = draggedAuthServer ?? "";
        var generation = ++_authServerGeneration;
        var knownOAuthSupport = draggedAuthServerOAuthSupported;
        draggedAuthServer = null;
        draggedAuthServerOAuthSupported = null;
        _oauthSupported = knownOAuthSupport;
        _UpdateOAuthEntryVisibility();
        // Password login remains the predictable default. OAuth is offered as
        // an explicit alternative after the server advertises a client ID.
        _SetLoginMode(false);
        if (knownOAuthSupport is null && !string.IsNullOrWhiteSpace(_authServerUrl))
        {
            var server = _authServerUrl;
            Dispatcher.BeginInvoke(new Func<Task>(async () =>
            {
                var supported = await IsOAuthSupportedAsync(server).ConfigureAwait(true);
                if (generation != _authServerGeneration ||
                    !string.Equals(_authServerUrl, server, StringComparison.OrdinalIgnoreCase) ||
                    IsAuthenticating)
                    return;
                _oauthSupported = supported;
                _UpdateOAuthEntryVisibility();
            }));
        }
    }

    private void BtnBack_Click(object sender, EventArgs e)
    {
        ProfileService.IsCreatingProfile = false;
        _authServerUrl = "";
        _authServerGeneration++;
        TextName.Text = null;
        TextPass.Password = null;
        ModMain.frmLaunchLeft.RefreshPage(true);
    }

    private void BtnLogin_Click(object sender, EventArgs e)
    {
        if (IsAuthenticating) return;
        if (string.IsNullOrWhiteSpace(_authServerUrl))
        {
            HintService.Hint(Lang.Text("Launch.Account.Auth.EmptyFields"), HintType.Error);
            return;
        }

        if (!_authServerUrl.IsMatch(RegexPatterns.HttpUri))
        {
            HintService.Hint(Lang.Text("Launch.Account.Auth.InvalidServer"), HintType.Error);
            return;
        }

        var useOAuth = _isOAuthMode;
        var authServerUrl = _authServerUrl;
        IsAuthenticating = true;
        BtnLogin.IsEnabled = false;
        BtnOAuth.IsEnabled = false;
        BtnBack.IsEnabled = false;
        BtnBackOAuth.IsEnabled = false;
        BtnUsePassword.IsEnabled = false;
        BtnUseOAuth.IsEnabled = false;
        BtnWebsite.IsEnabled = false;
        BtnPasswordWebsite.IsEnabled = false;
        Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            var keepControlsDisabled = false;
            try
            {
                ProfileService.IsCreatingProfile = true;
                if (useOAuth)
                {
                    if (!await _TryStartYggdrasilConnectAsync(authServerUrl).ConfigureAwait(true))
                    {
                        HintService.Hint(Lang.Text("Launch.Account.Auth.LoginFailed"), HintType.Error);
                        return;
                    }
                    keepControlsDisabled = true;
                    return;
                }
                if (string.IsNullOrWhiteSpace(TextName.Text) || string.IsNullOrWhiteSpace(TextPass.Password))
                {
                    HintService.Hint(Lang.Text("Launch.Account.Auth.EmptyFields"), HintType.Error);
                    return;
                }
                var loginData = new ModLaunch.McLoginServer(ModLaunch.McLoginType.Auth)
                {
                    BaseUrl = await ApiLocation.TryRequestAsync(authServerUrl).ConfigureAwait(true),
                    UserName = TextName.Text, Password = TextPass.Password, Description = "Authlib-Injector",
                    LoginType = ModLaunch.McLoginType.Auth
                };
                ModLaunch.mcLoginAuthLoader.Start(loginData, true);
                while (ModLaunch.mcLoginAuthLoader.State == ModBase.LoadState.Loading)
                {
                    BtnLogin.Text = Lang.Number(ModLaunch.mcLoginAuthLoader.Progress, "P0");
                    await Task.Delay(50);
                }

                switch (ModLaunch.mcLoginAuthLoader.State)
                {
                    case ModBase.LoadState.Finished:
                        ModMain.frmLaunchLeft.RefreshPage(true);
                        break;
                    case ModBase.LoadState.Aborted:
                        HintService.Hint(Lang.Text("Launch.Account.Auth.Cancelled"));
                        break;
                    case ModBase.LoadState.Waiting:
                    case ModBase.LoadState.Loading:
                    case ModBase.LoadState.Failed:
                    default:
                    {
                        if (ModLaunch.mcLoginAuthLoader.Error is null)
                            throw new InvalidOperationException(Lang.Text("Launch.Account.Microsoft.Error.Unknown"));
                        throw ModLaunch.mcLoginAuthLoader.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message == "$$")
                {
                }
                else if (ex.Message.StartsWith("$"))
                {
                    HintService.Hint(
                        Lang.Text("Launch.Account.Auth.LoginFailed.WithDetail",ex.Message.TrimStart('$')),
                        HintType.Error);
                }
                else if (ex is IdentityModelException identityModelException)
                {
                    _ShowIdentityModelFailure(identityModelException);
                }
                else
                {
                    ModBase.Log(
                        ex,
                        Lang.Text("Launch.Account.Auth.LoginFailed"),
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Launch.Account.Auth.LoginFailed"));
                }
            }
            finally
            {
                if (!keepControlsDisabled) _FinishLoginAttempt();
            }
        }));
    }

    private void _FinishLoginAttempt()
    {
        IsAuthenticating = false;
        BtnLogin.IsEnabled = true;
        BtnOAuth.IsEnabled = true;
        BtnBack.IsEnabled = true;
        BtnBackOAuth.IsEnabled = true;
        BtnUsePassword.IsEnabled = true;
        BtnUseOAuth.IsEnabled = true;
        BtnWebsite.IsEnabled = true;
        BtnPasswordWebsite.IsEnabled = true;
        BtnLogin.Text = Lang.Text("Launch.Account.Auth.Login");
    }

    private void _SetLoginMode(bool useOAuth)
    {
        if (_loginModeInitialized && _isOAuthMode == useOAuth)
            return;

        var wasOAuth = _loginModeInitialized && _isOAuthMode;
        _isOAuthMode = useOAuth;
        _loginModeInitialized = true;

        var oldElements = wasOAuth
            ? new FrameworkElement[] { PanOAuth, BtnUsePassword, BtnWebsite, BtnBackOAuth }
            : new FrameworkElement[] { TextName, TextNameTitle, TextPass, TextPassTitle, BtnLink, BtnLogin, PanPasswordLinks, BtnBack };
        var newElements = useOAuth
            ? new FrameworkElement[] { PanOAuth, BtnUsePassword, BtnWebsite, BtnBackOAuth }
            : new FrameworkElement[] { TextName, TextNameTitle, TextPass, TextPassTitle, BtnLink, BtnLogin, PanPasswordLinks, BtnBack };

        var animations = new List<ModAnimation.AniData>();
        if (wasOAuth || !_loginModeInitialized)
        {
            foreach (var element in oldElements)
            {
                if (element.Visibility == Visibility.Visible)
                    animations.Add(ModAnimation.AaOpacity(element, -element.Opacity, 90));
            }
        }

        animations.Add(ModAnimation.AaCode(() =>
        {
            foreach (var element in oldElements)
            {
                element.Visibility = Visibility.Collapsed;
                element.Opacity = 1d;
            }

            foreach (var element in newElements)
            {
                element.Visibility = ReferenceEquals(element, BtnLink) && !_hasRegisterLink
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                element.Opacity = 0d;
            }
        }, after: true));

        foreach (var element in newElements)
        {
            var targetOpacity = ReferenceEquals(element, BtnBackOAuth) || ReferenceEquals(element, PanPasswordLinks)
                ? 0.3d
                : 1d;
            animations.Add(ModAnimation.AaOpacity(element, targetOpacity, 130, 20,
                new ModAnimation.AniEaseInFluent()));
        }

        ModAnimation.AniStart(animations, "Profile Authentication Mode");
    }

    private void _UpdateOAuthEntryVisibility()
    {
        var isOAuthAvailable = _oauthSupported == true;
        BtnUseOAuth.Visibility = isOAuthAvailable ? Visibility.Visible : Visibility.Collapsed;
        BtnPasswordWebsite.SetValue(Grid.ColumnSpanProperty, isOAuthAvailable ? 1 : 3);
    }

    private async Task<bool> _TryStartYggdrasilConnectAsync(string authServerUrl)
    {
        var server = await ApiLocation.TryRequestAsync(authServerUrl).ConfigureAwait(true);
        using var response = await HttpRequest.Create(server).SendAsync().ConfigureAwait(true);
        if (!response.IsSuccess) return false;
        var metadata = await _TryReadJsonObjectAsync(response).ConfigureAwait(true);
        if (metadata is null) return false;
        var discovery = metadata["meta"]?["feature.openid_configuration_url"]?.ToString()
                        ?? metadata["feature.openid_configuration_url"]?.ToString();
        if (string.IsNullOrWhiteSpace(discovery)) return false;

        using var discoveryResponse = await HttpRequest.Create(discovery).SendAsync().ConfigureAwait(true);
        if (!discoveryResponse.IsSuccess) return false;
        var discoveryMetadata = await _TryReadJsonObjectAsync(discoveryResponse).ConfigureAwait(true);
        if (discoveryMetadata is null) return false;
        var clientId = _GetClientId(discoveryMetadata);
        if (string.IsNullOrWhiteSpace(clientId)) return false;

        ModBase.Log($"[Profile] Yggdrasil Connect client id resolved for {new Uri(server).Host}", ModBase.LogLevel.Debug);
        _ = _CompleteYggdrasilConnectLoginAsync(server, discovery, clientId);
        return true;
    }

    private async Task _CompleteYggdrasilConnectLoginAsync(string server, string discovery, string clientId)
    {
        try
        {
            var profile = await ProfileService.AuthenticateAsync(ProfileType.YggdrasilConnect, new AuthenticationRequest
            {
                Server = server,
                DiscoveryAddress = discovery,
                ClientId = clientId,
                DeviceCodeHandler = ProfileUi.ShowDeviceCodeLoginAsync,
                ProfileSelector = (candidates, _) => Task.FromResult(_SelectYggdrasilProfile(candidates))
            }, existing: null, select: true, token: CancellationToken.None).ConfigureAwait(false);
            ModBase.RunInUi(() =>
            {
                ProfileService.IsCreatingProfile = false;
                ModMain.frmLaunchLeft.RefreshPage(true);
            });
            LogWrapper.Info("Profile","Yggdrasil Connect 登录成功：" + profile.UserName);
        }
        catch (ThreadInterruptedException)
        {
            HintService.Hint(Lang.Text("Launch.Account.Auth.Cancelled"));
        }
        catch (Exception ex)
        {
            if (ex is IdentityModelException identityModelException)
                ModBase.RunInUi(() => _ShowIdentityModelFailure(identityModelException));
            else
                ModBase.Log(ex, Lang.Text("Launch.Account.Auth.LoginFailed"), ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Launch.Account.Auth.LoginFailed"));
        }
        finally
        {
            ModBase.RunInUi(_FinishLoginAttempt);
        }
    }

    private static AuthenticationCandidate? _SelectYggdrasilProfile(IReadOnlyList<AuthenticationCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        AuthenticationCandidate? selected = null;
        ModBase.RunInUiWait(() =>
        {
            var controls = candidates.Select(candidate => (IMyRadio)new MyRadioBox { Text = candidate.Name }).ToList();
            var index = ModMain.MyMsgBoxSelect(controls, Lang.Text("Launch.Account.Auth.ChangeProfile"));
            if (index is >= 0 and < 100000) selected = candidates[index.Value];
        });
        return selected;
    }

    internal static async Task<bool> IsOAuthSupportedAsync(string authServerUrl)
    {
        try
        {
            var server = await ApiLocation.TryRequestAsync(authServerUrl).ConfigureAwait(false);
            using var response = await HttpRequest.Create(server).SendAsync().ConfigureAwait(false);
            if (!response.IsSuccess) return false;
            var metadata = await _TryReadJsonObjectAsync(response).ConfigureAwait(false);
            if (metadata is null) return false;
            var discovery = metadata["meta"]?["feature.openid_configuration_url"]?.ToString()
                            ?? metadata["feature.openid_configuration_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(discovery)) return false;

            using var discoveryResponse = await HttpRequest.Create(discovery).SendAsync().ConfigureAwait(false);
            if (!discoveryResponse.IsSuccess) return false;
            var discoveryMetadata = await _TryReadJsonObjectAsync(discoveryResponse).ConfigureAwait(false);
            if (discoveryMetadata is null) return false;
            var clientId = _GetClientId(discoveryMetadata);
            var deviceEndpoint = discoveryMetadata["device_authorization_endpoint"]?.ToString();
            var tokenEndpoint = discoveryMetadata["token_endpoint"]?.ToString();
            return !string.IsNullOrWhiteSpace(clientId) &&
                   !string.IsNullOrWhiteSpace(deviceEndpoint) &&
                   !string.IsNullOrWhiteSpace(tokenEndpoint);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "检测 Yggdrasil Connect 支持失败", ModBase.LogLevel.Debug);
            return false;
        }
    }

    private static string? _GetClientId(JsonObject discoveryMetadata)
        => discoveryMetadata["shared_client_id"]?.ToString();

    private static async Task<JsonObject?> _TryReadJsonObjectAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.AsJsonAsync<JsonObject>().ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void _ShowIdentityModelFailure(IdentityModelException exception)
    {
        var detail = ExceptionDetails.GetUserReason(exception);
        HintService.Hint(
            string.IsNullOrWhiteSpace(detail)
                ? Lang.Text("Launch.Account.Auth.LoginFailed")
                : Lang.Text("Launch.Account.Auth.LoginFailed.WithDetail", detail),
            HintType.Error);
    }

    // 链接处理
    private void ComboName_TextChanged(object sender, TextChangedEventArgs e)
    {
        _isRegisterMode = string.IsNullOrEmpty(TextName.Text);
        BtnLink.Content = _isRegisterMode
            ? Lang.Text("Launch.Account.Auth.Register")
            : Lang.Text("Launch.Account.Auth.ForgotPassword");
    }

    private void Btn_Click(object sender, EventArgs e)
    {
        ModBase.OpenWebsite(_isRegisterMode
            ? Config.InstanceAuth.AuthRegisterAddress.ToString()
            : Config.InstanceAuth.AuthRegisterAddress.ToString().Replace("/auth/register", "/auth/forgot"));
    }

    private void BtnWebsite_Click(object sender, EventArgs e)
    {
        if (!Uri.TryCreate(_authServerUrl, UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("https" or "http")) return;
        var websiteUri = new UriBuilder(serverUri)
        {
            Path = "/",
            Query = "",
            Fragment = ""
        }.Uri;
        ModBase.OpenWebsite(websiteUri.ToString());
    }

    // 切换注册按钮可见性
    private void ReloadRegisterButton()
    {
        var address = Config.InstanceAuth.AuthRegisterAddress.ToString();
        _hasRegisterLink = new HttpValidator().Validate(address).IsValid;
        BtnLink.Visibility = !_isOAuthMode && _hasRegisterLink ? Visibility.Visible : Visibility.Collapsed;
    }

}
