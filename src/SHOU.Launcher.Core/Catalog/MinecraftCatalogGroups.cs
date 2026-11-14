using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Catalog;

public static class MinecraftCatalogGroups
{
    // Explicit identities: April 1 can also contain ordinary releases. Never invent missing manifest entries.
    private static readonly HashSet<string> AprilFools = new(StringComparer.OrdinalIgnoreCase)
    { "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "26w14a" };
    public static string Group(GameCatalogVersion version) => AprilFools.Contains(version.Id) ? "愚人节版" : version.Kind switch
    { "release" => "正式版", "snapshot" => "预览版", "old_beta" or "old_alpha" => "远古版", _ => "其他版本" };
}
