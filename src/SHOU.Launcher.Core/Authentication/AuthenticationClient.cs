using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

public sealed class AuthenticationException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }
public sealed record AuthSession(AccountProfile Profile, string AccessToken, string? RefreshToken = null,
    string? ClientToken = null, string? Xuid = null)
{ public override string ToString() => "AuthSession(" + Profile.Id + ")"; }
public sealed record MicrosoftEndpoints(Uri DeviceCode, Uri Token, Uri XboxLive, Uri Xsts, Uri MinecraftLogin, Uri Entitlements, Uri Profile)
{
    public static MicrosoftEndpoints Official { get; } = new(new("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"),
        new("https://login.microsoftonline.com/consumers/oauth2/v2.0/token"), new("https://user.auth.xboxlive.com/user/authenticate"),
        new("https://xsts.auth.xboxlive.com/xsts/authorize"), new("https://api.minecraftservices.com/authentication/login_with_xbox"),
        new("https://api.minecraftservices.com/entitlements/mcstore"), new("https://api.minecraftservices.com/minecraft/profile"));
}

public sealed class AuthenticationClient(HttpClient http, MicrosoftEndpoints? microsoftEndpoints = null)
{
    private readonly MicrosoftEndpoints endpoints = microsoftEndpoints ?? MicrosoftEndpoints.Official;
    public static string ResolveMicrosoftClientId(string? explicitId, string? userId, IBuildConfiguration build)
    {
        var value = new[] { explicitId, userId, build.MicrosoftClientId }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (value is null || !Guid.TryParse(value, out _)) throw new AuthenticationException("missing_client_id", "Microsoft Client ID未配置或格式不正确。");
        return value;
    }
    public async Task<AuthSession> LoginMicrosoftAsync(string clientId, IProgress<DeviceCodeChallenge> challenge, CancellationToken ct = default)
    {
        using var deviceRequest = new HttpRequestMessage(HttpMethod.Post, endpoints.DeviceCode) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = "XboxLive.signin offline_access" }) };
        var device = await SendAsync(deviceRequest, ct).ConfigureAwait(false);
        var code = Required(device, "device_code");
        var userCode = Required(device, "user_code");
        var verification = new Uri(Required(device, "verification_uri"));
        if (verification.Scheme != "https" && !verification.IsLoopback) throw new AuthenticationException("invalid_device_response", "授权地址不安全。");
        var expires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp((int?)device["expires_in"] ?? 900, 1, 1800));
        var interval = Math.Clamp((int?)device["interval"] ?? 5, 1, 60);
        challenge.Report(new(userCode, verification, expires, "请在浏览器完成Microsoft设备授权；可随时取消。"));
        while (DateTimeOffset.UtcNow < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            using var poll = new HttpRequestMessage(HttpMethod.Post, endpoints.Token) { Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = clientId, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = code }) };
            try { return await CompleteMicrosoftAsync(await SendAsync(poll, ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (AuthenticationException ex) when (ex.Code == "authorization_pending") { }
            catch (AuthenticationException ex) when (ex.Code == "slow_down") { interval = Math.Min(60, interval + 5); }
        }
        throw new AuthenticationException("expired_token", "设备授权已过期，请重新登录。");
    }
    public async Task<AuthSession> RefreshMicrosoftAsync(string clientId, AuthSession session, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new AuthenticationException("invalid_grant", "缺少刷新凭据，请重新登录。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Token) { Content = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken, ["scope"] = "XboxLive.signin offline_access" }) };
        var oauth = await SendAsync(request, ct).ConfigureAwait(false);
        oauth["refresh_token"] ??= session.RefreshToken;
        var refreshed = await CompleteMicrosoftAsync(oauth, ct).ConfigureAwait(false);
        if (refreshed.Profile.PlayerUuid != session.Profile.PlayerUuid) throw new AuthenticationException("profile_mismatch", "刷新返回了不同角色，原档案未覆盖。");
        return refreshed with { Profile = refreshed.Profile with { Id = session.Profile.Id } };
    }
    private async Task<AuthSession> CompleteMicrosoftAsync(JsonObject oauth, CancellationToken ct)
    {
        var access = Required(oauth, "access_token");
        var xbl = await PostJsonAsync(endpoints.XboxLive, new { Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + access }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" }, ct).ConfigureAwait(false);
        var xsts = await PostJsonAsync(endpoints.Xsts, new { Properties = new { SandboxId = "RETAIL", UserTokens = new[] { Required(xbl, "Token") } }, RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT" }, ct).ConfigureAwait(false);
        var hash = (string?)xsts["DisplayClaims"]?["xui"]?[0]?["uhs"] ?? throw new AuthenticationException("xsts_missing_hash", "Xbox授权缺少用户标识。");
        var minecraft = await PostJsonAsync(endpoints.MinecraftLogin, new { identityToken = "XBL3.0 x=" + hash + ";" + Required(xsts, "Token") }, ct).ConfigureAwait(false);
        var minecraftToken = Required(minecraft, "access_token");
        using var entitlementRequest = new HttpRequestMessage(HttpMethod.Get, endpoints.Entitlements);
        entitlementRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);
        var entitlement = await SendAsync(entitlementRequest, ct).ConfigureAwait(false);
        if (entitlement["items"] is not JsonArray items || !items.Any(item => (string?)item?["name"] is "product_minecraft" or "game_minecraft")) throw new AuthenticationException("minecraft_not_owned", "此账户没有Minecraft Java版许可。");
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, endpoints.Profile);
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftToken);
        var profile = await SendAsync(profileRequest, ct).ConfigureAwait(false);
        var uuid = NormalizeUuid(Required(profile, "id"));
        return new(new("microsoft:" + uuid, AccountKind.Microsoft, Required(profile, "name"), uuid,
            Status: "已授权 · 尚未持久保存", ExpiresAt: DateTimeOffset.UtcNow.AddSeconds((int?)minecraft["expires_in"] ?? 86400)),
            minecraftToken, (string?)oauth["refresh_token"], Xuid: (string?)xsts["DisplayClaims"]?["xui"]?[0]?["xid"] ?? (string?)xbl["DisplayClaims"]?["xui"]?[0]?["xid"]);
    }
    public async Task<IReadOnlyList<AuthSession>> LoginYggdrasilAsync(Uri server, string username, string password, CancellationToken ct = default,
        Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>>? selectProfile = null)
    {
        var root = NormalizeYggdrasilRoot(server);
        var clientToken = Guid.NewGuid().ToString("N");
        var response = await PostJsonAsync(new Uri(root + "/authserver/authenticate"), new { agent = new { name = "Minecraft", version = 1 }, username, password, clientToken, requestUser = true }, ct).ConfigureAwait(false);
        var profiles = response["availableProfiles"] as JsonArray ?? new JsonArray();
        if (profiles.Count == 0 && response["selectedProfile"] is JsonObject selected) profiles.Add(selected.DeepClone());
        if (profiles.Count == 0) throw new AuthenticationException("invalid_profile", "认证服务没有可用角色。");
        if (profiles.Count > 64) throw new AuthenticationException("invalid_profile", "认证服务返回过多角色。");
        JsonObject selectedProfile;
        var roleResult = response;
        if (response["selectedProfile"] is JsonObject bound)
        {
            // A bound token is not an authorization for every available profile.
            selectedProfile = bound;
        }
        else
        {
            var choices = profiles.Select(value => value as JsonObject ?? throw new AuthenticationException("invalid_profile", "角色数据格式不正确。"))
                .Select(profile => new AuthenticationProfileChoice(NormalizeUuid(Required(profile, "id")), Required(profile, "name"))).ToArray();
            var chosen = choices.Length == 1 ? choices[0].Id : selectProfile is not null
                ? await selectProfile(choices, ct).ConfigureAwait(false)
                : throw new AuthenticationException("profile_selection_required", "此登录需先选择一个角色。");
            ct.ThrowIfCancellationRequested();
            var choice = choices.FirstOrDefault(c => c.Id == chosen) ?? throw new AuthenticationException("profile_selection_cancelled", "角色选择已取消或无效。");
            selectedProfile = new JsonObject { ["id"] = choice.Id, ["name"] = choice.Name };
            // refresh revokes the unbound token. Bind exactly one chosen profile, once.
            roleResult = await PostJsonAsync(new Uri(root + "/authserver/refresh"), new { accessToken = Required(response, "accessToken"), clientToken = (string?)response["clientToken"] ?? clientToken,
                selectedProfile = new { id = choice.Id, name = choice.Name }, requestUser = true }, ct).ConfigureAwait(false);
        }
        var id = NormalizeUuid(Required(selectedProfile, "id"));
        if ((string?)roleResult["selectedProfile"]?["id"] is { } returned && NormalizeUuid(returned) != id) throw new AuthenticationException("invalid_profile", "认证服务返回了不同角色。");
        var stableId = "yggdrasil:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..12].ToLowerInvariant() + ":" + id;
        return [new(new(stableId, AccountKind.Yggdrasil, Required(selectedProfile, "name"), id, root, Status: "已授权 · 尚未持久保存"), Required(roleResult, "accessToken"), ClientToken: (string?)roleResult["clientToken"] ?? clientToken)];
    }
    public async Task<AuthSession> RefreshYggdrasilAsync(AuthSession session, CancellationToken ct = default)
    {
        var root = NormalizeYggdrasilRoot(new Uri(session.Profile.AuthenticationServer ?? throw new AuthenticationException("missing_server", "档案没有认证服务地址。")));
        var result = await PostJsonAsync(new Uri(root + "/authserver/refresh"), new { accessToken = session.AccessToken, clientToken = session.ClientToken,
            requestUser = true }, ct).ConfigureAwait(false);
        if ((string?)result["selectedProfile"]?["id"] is { } id && NormalizeUuid(id) != session.Profile.PlayerUuid) throw new AuthenticationException("invalid_profile", "刷新返回了不同角色。");
        return session with { AccessToken = Required(result, "accessToken"), ClientToken = (string?)result["clientToken"] ?? session.ClientToken };
    }
    public static string NormalizeYggdrasilRoot(Uri server)
    {
        if (!server.IsAbsoluteUri || (server.Scheme != "https" && !server.IsLoopback) || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment)) throw new AuthenticationException("invalid_server", "认证服务须为不含凭据的HTTPS地址。");
        var root = server.AbsoluteUri.TrimEnd('/');
        return root.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase) ? root[..^11] : root;
    }
    private async Task<JsonObject> PostJsonAsync(Uri uri, object value, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(value, options: new JsonSerializerOptions()) };
        return await SendAsync(message, ct).ConfigureAwait(false);
    }
    private async Task<JsonObject> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new AuthenticationException("oversized_response", "认证响应超过上限。");
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (text.Length > 2 * 1024 * 1024) throw new AuthenticationException("oversized_response", "认证响应超过上限。");
        JsonObject body;
        try { body = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new AuthenticationException("invalid_response", $"认证服务返回非JSON响应（HTTP {(int)response.StatusCode}）。"); }
        if (!response.IsSuccessStatusCode || body["error"] is not null)
        {
            var rawCode = (string?)body["error"] ?? (body["XErr"] is not null ? "xsts_" + body["XErr"] : "http_" + (int)response.StatusCode);
            var code = new string(rawCode.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(80).ToArray());
            throw new AuthenticationException(code, $"认证失败：{code}（HTTP {(int)response.StatusCode}）。");
        }
        return body;
    }
    private static string Required(JsonObject value, string key) => (string?)value[key] is { Length: > 0 } result ? result : throw new AuthenticationException("invalid_response", "认证响应缺少必要字段：" + key);
    private static string NormalizeUuid(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : throw new AuthenticationException("invalid_profile", "角色UUID格式不正确。");
}
