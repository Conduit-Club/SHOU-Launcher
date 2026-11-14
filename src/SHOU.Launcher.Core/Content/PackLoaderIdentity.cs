using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Content;

internal static class PackLoaderIdentity
{
    internal static bool Supported(string loader) => loader is "vanilla" or "fabric" or "quilt" or "forge" or "neoforge";
    internal static void Validate(string loader, string? version)
    {
        if (!Supported(loader)) throw new NotSupportedException("此加载器的实例安装链尚未接入：" + loader);
        if (loader != "vanilla") SafePath.Identifier(version ?? throw new InvalidDataException("加载器版本身份缺失。"));
    }
    internal static void ValidateShou(string loader, string? version)
    {
        if (loader is "cleanroom" or "legacyfabric" || Loaders.PortableVendorInstaller.Supports(loader)) SafePath.Identifier(version ?? throw new InvalidDataException("加载器版本缺失。"));
        else Validate(loader, version);
    }
    internal static string Dependency(string loader) => loader switch { "fabric" => "fabric-loader", "quilt" => "quilt-loader", "forge" => "forge", "neoforge" => "neoforge", _ => throw new NotSupportedException() };
}
