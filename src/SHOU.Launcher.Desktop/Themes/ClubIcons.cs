namespace SHOU.Launcher.Desktop.Themes;

/// <summary>Semantic names from the shared, licensed Lucide 24-unit SVG set.</summary>
public static class ClubIcons
{
    public const string Cube = "box", Puzzle = "puzzle", Document = "file-archive", Layers = "layers",
        Sparkles = "sparkles", Globe = "globe", Heart = "heart", Cloth = "scroll", Fox = "cat",
        Link = "link-2", Toolbox = "tool-case", Chat = "message-circle", Download = "download",
        Folder = "folder-open", Settings = "settings";
    public static string ForDownloadCategory(string key) => key switch
    {
        "minecraft" => "boxes", "installer-minecraft" => "package", "mod" => Puzzle,
        "modpack" => "package", "datapack" => Document, "resourcepack" => Layers,
        "shader" => Sparkles, "world" => Globe, "favorites" => Heart, "tasks" => Download,
        "installer-fabric" or "installer-legacyfabric" => Cloth,
        "installer-neoforge" or "installer-labymod" => Fox, "installer-optifine" => "gauge",
        "installer-forge" => "anvil", "installer-cleanroom" => "flask-conical",
        "installer-liteloader" => "egg", _ => Cube
    };
    public static string ForInstanceIcon(string? key) => key switch
    { "fabric" => Cloth, "quilt" => Layers, "forge" => "anvil", "neoforge" => Fox, _ => Cube };
    public static string ForInstanceSection(string key) => key switch
    {
        "overview" => "layout-dashboard", "settings" => Settings, "modify" => "wrench",
        "export" => "package", "worlds" => Globe, "selection" => Folder, "screenshots" => "image",
        "mods" => Puzzle, "resourcepacks" => Layers, "shaders" => Sparkles,
        "schematics" => "file-box", "servers" => "server", "relations" => "git-pull-request-arrow", _ => Cube
    };
}
