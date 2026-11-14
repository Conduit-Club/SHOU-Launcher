using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

public sealed partial class AccountAppearanceClient
{
    public async Task<AccountAppearanceWriteResult> ChangeMicrosoftAsync(AccountProfile account, string accessToken, AccountAppearanceChange change,
        Func<AccountAppearance, CancellationToken, Task> beforeWrite, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Microsoft || string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("请先登录此 Microsoft 角色。");
        if (!Enum.IsDefined(change.Action) || !Enum.IsDefined(change.Model)) throw new InvalidDataException("外观操作或模型无效。");
        if (change.Png.Length > AccountAppearancePng.MaximumPngBytes) throw new InvalidDataException("皮肤 PNG 超过 2 MiB 上限。");
        AccountTextureImage? input = null; string? capeId = null;
        if (change.Action == AccountAppearanceAction.UploadSkin)
        {
            input = AccountAppearancePng.Decode(change.Png.ToArray());
            if (input.Height == 32 && change.Model == AccountSkinModel.Slim) throw new InvalidDataException("64×32 旧版皮肤仅支持经典模型。");
            if (change.CapeId is not null) throw new InvalidDataException("上传皮肤不能同时修改披风。");
        }
        else
        {
            if (!change.Png.IsEmpty) throw new InvalidDataException("披风操作不能携带皮肤文件。");
            if (change.Action == AccountAppearanceAction.WearCape) capeId = Guid.TryParse(change.CapeId, out var guid) ? guid.ToString() : throw new InvalidDataException("请选择拥有的披风。");
            else if (change.CapeId is not null) throw new InvalidDataException("卸下披风不需要披风标识。");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); var token = timeout.Token;
        AccountAppearance current;
        try { current = await ReadMicrosoftAsync(account, accessToken, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { throw new IOException("无法读取官方外观，请刷新登录或稍后重试。"); }
        bool SameSkin(AccountAppearance a) => input is not null && a.Model == change.Model && a.Skin is { } skin && skin.Width == input.Width && skin.Height == input.Height && skin.Rgba.Span.SequenceEqual(input.Rgba.Span);
        bool SameCape(AccountCapeAppearance cape) => Guid.TryParse(cape.Id, out var id) && id.ToString() == capeId;
        if (change.Action == AccountAppearanceAction.WearCape && !current.Capes.Any(SameCape)) throw new InvalidDataException("此角色不拥有所选披风，未发送修改请求。");
        if (change.Action == AccountAppearanceAction.UploadSkin && SameSkin(current) || change.Action == AccountAppearanceAction.WearCape && current.Capes.Any(c => SameCape(c) && c.IsActive) || change.Action == AccountAppearanceAction.HideCape && !current.Capes.Any(c => c.IsActive))
            return new(AccountAppearanceWriteState.Verified, current, "当前已是所选外观。" );
        await beforeWrite(current, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(change.Action switch { AccountAppearanceAction.UploadSkin => HttpMethod.Post, AccountAppearanceAction.WearCape => HttpMethod.Put, _ => HttpMethod.Delete },
            "https://api.minecraftservices.com/minecraft/profile/" + (change.Action == AccountAppearanceAction.UploadSkin ? "skins" : "capes/active"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); request.Headers.Accept.ParseAdd("application/json");
        if (input is not null)
        {
            var form = new MultipartFormDataContent(); form.Add(new StringContent(change.Model == AccountSkinModel.Slim ? "slim" : "classic"), "variant");
            var file = new ByteArrayContent(input.Png.ToArray()); file.Headers.ContentType = new("image/png"); form.Add(file, "file", "skin.png"); request.Content = form;
        }
        else if (capeId is not null) request.Content = new StringContent(JsonSerializer.Serialize(new { capeId }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final != request.RequestUri || (int)response.StatusCode is >= 300 and < 400)
                return new(AccountAppearanceWriteState.OutcomeUnknown, null, "外观服务发生重定向，已停止后续请求。请刷新外观核对结果。");
            if ((int)response.StatusCode >= 500) return new(AccountAppearanceWriteState.OutcomeUnknown, null, $"外观服务暂不可用（HTTP {(int)response.StatusCode}），修改结果未确认。请刷新外观核对。");
            if (!response.IsSuccessStatusCode) return new(AccountAppearanceWriteState.Rejected, null, $"外观修改未被接受（HTTP {(int)response.StatusCode}）。请刷新登录或稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return new(AccountAppearanceWriteState.OutcomeUnknown, null, "修改请求已发出，结果未能确认。请刷新外观后再操作。"); }
        try
        {
            var updated = await ReadMicrosoftAsync(account, accessToken, token).ConfigureAwait(false);
            var verified = change.Action switch { AccountAppearanceAction.UploadSkin => SameSkin(updated), AccountAppearanceAction.WearCape => updated.Capes.Any(c => SameCape(c) && c.IsActive), _ => !updated.Capes.Any(c => c.IsActive) };
            return verified ? new(AccountAppearanceWriteState.Verified, updated, "在线外观已更新。") : new(AccountAppearanceWriteState.AcceptedNeedsRefresh, null, "服务已接收修改，尚未读到新外观。请稍后刷新。");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { return new(AccountAppearanceWriteState.AcceptedNeedsRefresh, null, "服务已接收修改，刷新暂未完成。请稍后刷新外观。"); }
    }
}
