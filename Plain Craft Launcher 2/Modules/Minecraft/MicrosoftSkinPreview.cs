using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Media.Imaging;
using PCL.Core.IO.Net;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Minecraft.Skin;

namespace PCL;

public sealed record SkinPreviewData(BitmapSource Skin, BitmapSource? Cape, bool IsSlim,
    bool IsDefaultSkin = false, bool CapeLoadFailed = false);

/// <summary>Loads public equipped textures for a snapshot of the selected Microsoft profile.</summary>
public static class MicrosoftSkinPreview
{
    public static async Task<SkinPreviewData> LoadAsync(McProfile profile, CancellationToken cancellationToken)
    {
        if (profile.ProfileType != ProfileType.Microsoft || !Guid.TryParse(profile.Uuid, out var uuid))
            throw new ArgumentException("请选择有效的微软正版账户。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var client = NetworkService.GetClient(NetworkService.MojangSession);
        var response = await client.GetStringAsync("session/minecraft/profile/" + uuid.ToString("N"), timeout.Token)
            .ConfigureAwait(false);
        var textures = SkinResponseParser.ParseTextures(response);
        var isDefault = textures.SkinUrl is null;
        var defaultModel = ModSkin.McSkinSex(uuid.ToString("N"));
        var skin = isDefault
            ? LoadDefaultSkin(defaultModel)
            : await LoadTextureAsync(textures.SkinUrl!, timeout.Token).ConfigureAwait(false);
        ValidateSize(skin, isCape: false);

        BitmapSource? cape = null;
        var capeLoadFailed = false;
        if (textures.CapeUrl is not null)
        {
            try
            {
                cape = await LoadTextureAsync(textures.CapeUrl, timeout.Token).ConfigureAwait(false);
                ValidateSize(cape, isCape: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // A failed cape download must not hide a successfully loaded skin or claim no cape is equipped.
                cape = null;
                capeLoadFailed = true;
                ModBase.Log(ex, "[SkinPreview] 披风加载失败", ModBase.LogLevel.Debug);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new SkinPreviewData(skin, cape, isDefault ? defaultModel == "Alex" : textures.IsSlim,
            isDefault, capeLoadFailed);
    }

    private static BitmapSource LoadDefaultSkin(string model)
    {
        var bitmap = new BitmapImage(new Uri(ModBase.pathImage + "Skins/" + model + ".png"));
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task<BitmapSource> LoadTextureAsync(string address, CancellationToken cancellationToken)
    {
        // The public session API still returns HTTP texture URLs. Upgrade the exact Mojang host.
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net"
            || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("正版账户返回了无效的皮肤地址。");
        var secureUri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        using var response = await NetworkService.GetClient().GetAsync(secureUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("皮肤贴图过大。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + length > maxBytes) throw new InvalidDataException("皮肤贴图过大。");
            output.Write(buffer, 0, length);
        }
        output.Position = 0;
        var bitmap = new PngBitmapDecoder(output, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze();
        return bitmap;
    }

    public static void ValidateSize(BitmapSource bitmap, bool isCape)
    {
        if (bitmap.PixelWidth is < 64 or > 2048 || bitmap.PixelWidth % 64 != 0
            || (isCape ? bitmap.PixelHeight != bitmap.PixelWidth / 2
                : bitmap.PixelHeight != bitmap.PixelWidth && bitmap.PixelHeight != bitmap.PixelWidth / 2))
            throw new InvalidDataException(isCape ? "披风贴图尺寸无效。" : "皮肤贴图尺寸无效。");
    }
}
