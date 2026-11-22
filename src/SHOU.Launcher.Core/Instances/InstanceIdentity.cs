using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

public static class InstanceIdentity
{
    public static bool SupportsModDirectory(string loader) => loader is "fabric" or "legacyfabric" or "quilt" or "forge" or "neoforge" or "cleanroom" or "liteloader";
    public static string GameVersion(GameInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.GameVersionId)) return SafePath.Identifier(instance.GameVersionId);
        if (instance.Loader == "vanilla") return SafePath.Identifier(instance.VersionId);
        if (instance.Loader == "fabric" && instance.LoaderVersion is { Length: > 0 } loader)
        {
            var prefix = "fabric-loader-" + loader + "-";
            if (instance.VersionId.StartsWith(prefix, StringComparison.Ordinal)) return SafePath.Identifier(instance.VersionId[prefix.Length..]);
        }
        throw new InvalidDataException("此加载器实例缺少原版版本信息，请重新导入或创建实例。");
    }
}
