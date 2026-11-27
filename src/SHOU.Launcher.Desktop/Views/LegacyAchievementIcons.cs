namespace SHOU.Launcher.Desktop.Views;

/// <summary>Original achievement-generator IDs (1–39), not Minecraft numeric block/item IDs.</summary>
public static class LegacyAchievementIcons
{
    public static IReadOnlyList<string> Names { get; } = ["grass", "diamond", "sword_diamond", "creeper", "pig", "tnt", "cookie", "heart", "bed", "cake", "sign", "rail", "crafting_table", "redstone", "fire", "cobweb", "chest", "furnace", "book", "stone", "planks", "iron", "gold", "door_wood", "door_iron", "chestplate_diamond", "flint_and_steel", "potion", "splash_potion", "spawn_egg", "coal", "sword_iron", "bow", "arrow", "chestplate_iron", "bucket", "bucket_water", "bucket_lava", "bucket_milk"];
    public static string? Resolve(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (int.TryParse(value, out var id) && id is >= 1 and <= 39) return Names[id - 1];
        value = value switch { "grass_block" => "grass", "diamond_sword" => "sword_diamond", "iron_sword" => "sword_iron", "iron_ingot" => "iron", "gold_ingot" => "gold", "diamond_chestplate" => "chestplate_diamond", "iron_chestplate" => "chestplate_iron", _ => value };
        return Names.Contains(value) ? value : null;
    }
}
