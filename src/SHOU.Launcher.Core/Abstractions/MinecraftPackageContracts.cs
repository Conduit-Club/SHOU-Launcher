namespace SHOU.Launcher.Core.Abstractions;

public sealed record MinecraftPackage(string Kind, string FileName, long Size, string Sha1)
{
    public string Title => Kind == "client" ? "Minecraft 客户端" : "Minecraft 服务端";
}

/// <summary>Standalone official game files. This operation never creates an instance or runs Java.</summary>
public interface IMinecraftPackageService
{
    Task<IReadOnlyList<MinecraftPackage>> GetMinecraftPackagesAsync(string version, CancellationToken ct = default);
    Task DownloadMinecraftPackageAsync(string version, string kind, string destination, CancellationToken ct = default);
}
