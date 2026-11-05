using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

/// <summary>Only profile GETs and anonymous texture GETs. The caller supplies a client with redirects disabled.</summary>
public sealed class AccountAppearanceClient(HttpClient http)
{
    private const int MaximumJsonBytes = 256 * 1024, MaximumTextures = 64, MaximumTotalPngBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static Uri? ManagementUri(AccountProfile account) => account.Kind switch
    {
        AccountKind.Microsoft => new("https://www.minecraft.net/en-us/msaprofile/mygames/editskin"),
        AccountKind.Yggdrasil when Uri.TryCreate(account.AuthenticationServer, UriKind.Absolute, out var root) && root.Scheme == "https" && root.UserInfo.Length == 0
            => root.Host switch { "littleskin.cn" => new("https://littleskin.cn/"), "skin.mualliance.ltd" => new("https://skin.mualliance.ltd/"), _ => new Uri(root.GetLeftPart(UriPartial.Authority) + "/") },
        _ => null
    };
    public static AccountAppearance Unread(AccountProfile account) => new(account.Id, AccountSkinModel.Classic, null, [], false, ManagementUri(account),
        "点击“刷新头像”读取此角色的真实皮肤与披风；修改请使用对应官网。");
    public async Task<AccountAppearance> ReadMicrosoftAsync(AccountProfile account, string accessToken, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Microsoft || string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("请先登录此 Microsoft 角色。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        using var json = await JsonAsync(new("https://api.minecraftservices.com/minecraft/profile"), accessToken, ct);
        var profile = json.RootElement; RequireIdentity(profile, "id", account.PlayerUuid);
        var skins = Array(profile, "skins", 16); var capes = Array(profile, "capes", MaximumTextures);
        var activeSkin = skins.Where(s => Text(s, "state", 32) == "ACTIVE").ToArray();
        if (activeSkin.Length > 1) throw Invalid("角色返回多个启用的皮肤。");
        var model = activeSkin.Length == 0 ? AccountSkinModel.Classic : Model(Text(activeSkin[0], "variant", 32));
        var budget = new TextureBudget(); AccountTextureImage? skin = null;
        if (activeSkin.Length != 0) skin = await TextureAsync(OfficialTexture(Text(activeSkin[0], "url", 2048)), false, budget, ct);
        var result = new List<AccountCapeAppearance>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cape in capes)
        {
            var id = Text(cape, "id", 128); if (!Guid.TryParse(id, out _) || !ids.Add(id)) throw Invalid("披风标识无效或重复。");
            var texture = await TextureAsync(OfficialTexture(Text(cape, "url", 2048)), true, budget, ct);
            result.Add(new(id, Text(cape, "alias", 128, false) is { Length: > 0 } name ? name : "披风 " + id[..8], Text(cape, "state", 32) == "ACTIVE", texture));
        }
        if (result.Count(c => c.IsActive) > 1) throw Invalid("角色返回多个启用的披风。");
        return new(account.Id, model, skin, result, false, ManagementUri(account), "已读取 Minecraft 官方档案；皮肤上传与披风切换请前往官网。", true);
    }
    public async Task<AccountAppearance> ReadYggdrasilAsync(AccountProfile account, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Yggdrasil || account.AuthenticationServer is null) throw new InvalidOperationException("此角色没有对应认证服务。");
        var root = new Uri(AuthenticationClient.NormalizeYggdrasilRoot(new Uri(account.AuthenticationServer)) + "/");
        if (root.Scheme != "https") throw Invalid("外观读取只允许 HTTPS 认证服务。");
        if (!Guid.TryParse(account.PlayerUuid, out var uuid)) throw Invalid("角色 UUID 无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        // These public endpoints do not receive any access/client/refresh token.
        using var metadata = await JsonAsync(root, null, ct);
        var domains = Array(metadata.RootElement, "skinDomains", 128).Select(e => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 253 } domain
            ? ValidateDomain(domain) : throw Invalid("纹理域名列表无效。")).ToArray();
        using var profileDocument = await JsonAsync(new(root, "sessionserver/session/minecraft/profile/" + uuid.ToString("N") + "?unsigned=true"), null, ct);
        var profile = profileDocument.RootElement; RequireIdentity(profile, "id", account.PlayerUuid);
        var properties = Array(profile, "properties", 64); var textures = properties.Where(p => Text(p, "name", 128) == "textures").ToArray();
        if (textures.Length == 0) return Unread(account) with { Capability = "此认证服务未返回当前角色的纹理。", IsRemote = true };
        if (textures.Length != 1) throw Invalid("角色返回重复纹理属性。");
        var encoded = Text(textures[0], "value", MaximumJsonBytes); byte[] decoded;
        try { decoded = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw Invalid("角色纹理 Base64 无效。"); }
        if (decoded.Length > MaximumJsonBytes) throw Invalid("角色纹理属性超过上限。");
        using var payload = ParseJson(decoded); var body = payload.RootElement; RequireIdentity(body, "profileId", account.PlayerUuid);
        var map = body.GetProperty("textures"); if (map.ValueKind != JsonValueKind.Object) throw Invalid("角色纹理映射无效。");
        var budget = new TextureBudget(); AccountTextureImage? skin = null; var model = AccountSkinModel.Classic; var capes = new List<AccountCapeAppearance>();
        if (map.TryGetProperty("SKIN", out var skinValue))
        {
            if (skinValue.TryGetProperty("metadata", out var skinMetadata) && skinMetadata.TryGetProperty("model", out _)) model = Model(Text(skinMetadata, "model", 32));
            skin = await TextureAsync(YggTexture(Text(skinValue, "url", 2048), domains), false, budget, ct);
        }
        if (map.TryGetProperty("CAPE", out var capeValue))
        {
            var uri = YggTexture(Text(capeValue, "url", 2048), domains); var texture = await TextureAsync(uri, true, budget, ct);
            capes.Add(new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant(), "当前披风", true, texture));
        }
        return new(account.Id, model, skin, capes, false, ManagementUri(account), "已读取此认证服务的公开纹理；可见披风仅为当前披风，修改请前往官网。", true);
    }
    private async Task<JsonDocument> JsonAsync(Uri uri, string? bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
        {
            if (uri != new Uri("https://api.minecraftservices.com/minecraft/profile")) throw Invalid("拒绝向未知端点发送账户凭据。");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        request.Headers.Accept.ParseAdd("application/json");
        return ParseJson(await SendBoundedAsync(request, MaximumJsonBytes, ct));
    }
    private static JsonDocument ParseJson(byte[] bytes)
    {
        try { _ = StrictUtf8.GetCharCount(bytes); return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException) { throw Invalid("外观服务返回无效的 UTF-8 JSON。"); }
    }
    private async Task<AccountTextureImage> TextureAsync(Uri uri, bool cape, TextureBudget budget, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Accept.ParseAdd("image/png");
        var bytes = await SendBoundedAsync(request, Math.Min(AccountAppearancePng.MaximumPngBytes, MaximumTotalPngBytes - budget.Bytes), ct);
        var image = AccountAppearancePng.Decode(bytes, cape); budget.Bytes += bytes.Length; budget.Pixels += image.Width * image.Height;
        if (budget.Pixels > 4 * 1024 * 1024) throw Invalid("外观图像总像素超过预算。");
        return image;
    }
    private async Task<byte[]> SendBoundedAsync(HttpRequestMessage request, int maximum, CancellationToken ct)
    {
        if (maximum <= 0) throw Invalid("外观下载超过总预算。");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest || response.RequestMessage?.RequestUri is { } final && final != request.RequestUri) throw Invalid("外观服务重定向已拒绝。");
        if (!response.IsSuccessStatusCode) throw Invalid($"读取外观失败（HTTP {(int)response.StatusCode}）。请刷新登录或稍后重试。");
        if (response.Content.Headers.ContentLength is { } length && length > maximum) throw Invalid("外观响应超过大小上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), ct).ConfigureAwait(false); if (read == 0) break; if (output.Length + read > maximum) throw Invalid("外观响应超过大小上限。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static Uri OfficialTexture(string value)
    {
        var uri = UriValue(value); if (uri.Host != "textures.minecraft.net" || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal) || uri.Query.Length != 0) throw Invalid("官方纹理地址不在允许范围。");
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }
    private static Uri YggTexture(string value, string[] domains)
    {
        var uri = UriValue(value); var host = uri.IdnHost;
        if (!uri.IsDefaultPort || !(host.EndsWith(".minecraft.net", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".mojang.com", StringComparison.OrdinalIgnoreCase) || domains.Any(d => d[0] == '.' ? host.EndsWith(d, StringComparison.OrdinalIgnoreCase) : host.Equals(d, StringComparison.OrdinalIgnoreCase)))) throw Invalid("纹理来源不在此认证服务的域名列表中。");
        return uri.Scheme == "http" && host == "textures.minecraft.net" ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri : uri.Scheme == "https" ? uri : throw Invalid("纹理来源须使用 HTTPS。");
    }
    private static Uri UriValue(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback) throw Invalid("纹理地址无效。");
        return uri;
    }
    private static string ValidateDomain(string domain)
    { var value = domain.StartsWith('.') ? domain[1..] : domain; if (value.Length == 0 || Uri.CheckHostName(value) != UriHostNameType.Dns || value.Contains('*')) throw Invalid("纹理域名规则无效。"); return domain; }
    private static AccountSkinModel Model(string value) => value.ToLowerInvariant() switch { "slim" => AccountSkinModel.Slim, "classic" or "default" or "" => AccountSkinModel.Classic, _ => throw Invalid("不支持此皮肤模型。") };
    private static void RequireIdentity(JsonElement value, string property, string uuid)
    { if (!Guid.TryParse(Text(value, property, 64), out var actual) || !Guid.TryParse(uuid, out var expected) || actual != expected) throw Invalid("外观服务返回了不同角色，当前外观已保留。"); }
    private static JsonElement[] Array(JsonElement value, string key, int maximum)
    {
        if (!value.TryGetProperty(key, out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw Invalid("外观列表类型或数量无效。");
        return array.EnumerateArray().ToArray();
    }
    private static string Text(JsonElement value, string property, int maximum, bool required = true)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { } result && result.Length <= maximum && !result.Any(c => char.IsControl(c))) return result;
        if (!required && !value.TryGetProperty(property, out _)) return "";
        throw Invalid("外观响应字段无效：" + property);
    }
    private static InvalidDataException Invalid(string message) => new(message);
    private sealed class TextureBudget { public int Bytes; public int Pixels; }
}
