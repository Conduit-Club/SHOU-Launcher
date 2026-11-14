namespace SHOU.Launcher.Core.Abstractions;

public enum AccountSkinModel { Classic, Slim }

/// <summary>Validated, bounded PNG and straight RGBA pixels. No native image decoder is needed in Core.</summary>
public sealed class AccountTextureImage
{
    private readonly byte[] png, rgba;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Png => png;
    public ReadOnlyMemory<byte> Rgba => rgba;
    internal AccountTextureImage(byte[] png, int width, int height, byte[] rgba)
    { this.png = png; Width = width; Height = height; this.rgba = rgba; }
    public override string ToString() => $"PNG {Width}×{Height}";
}

public sealed record AccountCapeAppearance(string Id, string Name, bool IsActive, AccountTextureImage? Texture);
public sealed record AccountAppearance(string AccountId, AccountSkinModel Model, AccountTextureImage? Skin,
    IReadOnlyList<AccountCapeAppearance> Capes, bool CanSetLocalSkin, Uri? ManagementUri, string Capability,
    bool IsRemote = false);

/// <summary>Local launcher appearance is deliberately separate from authentication and game launch settings.</summary>
public interface IAccountAppearanceService
{
    /// <param name="refreshRemote">Only an explicit refresh contacts the account's own profile service.</param>
    Task<AccountAppearance> GetAccountAppearanceAsync(string accountId, bool refreshRemote = false, CancellationToken cancellationToken = default);
    /// <summary>Saves a launcher-local offline skin; this does not alter vanilla game authentication/texture properties.</summary>
    Task<AccountAppearance> SaveOfflineAccountSkinAsync(string accountId, ReadOnlyMemory<byte> png, AccountSkinModel model,
        CancellationToken cancellationToken = default);
}
