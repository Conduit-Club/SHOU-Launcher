namespace SHOU.Launcher.Desktop.Themes;

/// <summary>Small native path outlines shared by page navigation; no platform font glyphs required.</summary>
public static class ClubIcons
{
    public const string Cube = "M9,0 L17,4 L17,13 L9,18 L1,13 L1,4 Z M1,4 L9,9 L17,4 M9,9 L9,18";
    public const string Puzzle = "M1,3 H6 C6,-1 12,-1 12,3 H17 V8 C21,8 21,14 17,14 V19 H12 C12,15 6,15 6,19 H1 V14 C5,14 5,8 1,8 Z";
    public const string Document = "M3,1 H11 L16,6 V19 H3 Z M11,1 V6 H16 M6,10 H10 M6,13 H13 M6,16 H11";
    public const string Layers = "M1,6 L10,1 L19,6 L10,11 Z M1,10 L10,15 L19,10 M1,14 L10,19 L19,14";
    public const string Sparkles = "M9,0 L11,6 L17,8 L11,10 L9,16 L7,10 L1,8 L7,6 Z M16,0 V4 M14,2 H18 M2,15 V19 M0,17 H4";
    public const string Globe = "M19,10 A9,9 0 1 1 1,10 A9,9 0 1 1 19,10 Z M1,10 H19 M10,1 C4,6 4,14 10,19 C16,14 16,6 10,1 Z";
    public const string Heart = "M10,18 L2,10 C-4,2 5,-3 10,4 C15,-3 24,2 18,10 Z";
    public const string Cloth = "M5,1 H18 V18 H5 M5,1 A3,3 0 0 0 5,7 H13 M5,18 A3,3 0 0 1 5,12 H13 M13,7 V12";
    public const string Fox = "M2,2 L7,5 H13 L18,2 V11 C18,21 2,21 2,11 Z M6,10 H7 M13,10 H14 M8,14 L10,16 L12,14";
    public const string Link = "M7,3 H5 A5,5 0 0 0 5,13 H7 M11,3 H13 A5,5 0 0 1 13,13 H11 M5,8 H13";
    public const string Toolbox = "M3,7 H15 V17 H3 Z M1,4 H17 V7 H1 Z M9,7 V17 M9,4 C3,-2 2,4 9,4 C16,-2 16,4 9,4";
    public const string Chat = "M2,2 H18 V14 H11 L7,18 V14 H2 Z M5,8 H7 M9,8 H11 M13,8 H15";
    public const string Download = "M10,1 V13 M4,7 L10,13 L16,7 M1,12 V19 H19 V12";
    public const string Folder = "M1,5 H8 L10,7 H21 V19 H1 Z M1,5 V2 H8 L10,5";
    public const string Settings = "M7,1 H10 L11,4 L13,5 L16,4 L18,7 L16,9 V11 L18,13 L16,16 L13,15 L11,16 L10,19 H7 L6,16 L4,15 L1,16 L-1,13 L1,11 V9 L-1,7 L1,4 L4,5 L6,4 Z M12,10 A3.5,3.5 0 1 1 5,10 A3.5,3.5 0 1 1 12,10";

    public static string ForDownloadCategory(string key) => key switch
    {
        "mod" => Puzzle,
        "modpack" or "minecraft" or "installer-minecraft" => Cube,
        "datapack" => Document,
        "resourcepack" => Layers,
        "shader" => Sparkles,
        "world" => Globe,
        "favorites" => Heart,
        "tasks" => Download,
        "installer-fabric" or "installer-legacyfabric" => Cloth,
        "installer-neoforge" or "installer-labymod" => Fox,
        "installer-optifine" => "M1,17 V12 A9,9 0 0 1 19,12 V17 M10,12 L15,6",
        "installer-forge" => "M1,2 H19 V5 L14,8 H12 V13 L17,16 V19 H3 V16 L8,13 V8 L3,5 H1 Z",
        "installer-cleanroom" => "M7,1 H13 M8,1 V8 L2,18 H18 L12,8 V1 M5,13 H15",
        "installer-liteloader" => "M3,18 L16,2 C22,-1 22,7 16,12 L10,16 H6 M7,12 H11 M11,8 H15",
        _ => Cube
    };

    public static string ForInstanceIcon(string? key) => key switch
    {
        "fabric" => Cloth, "quilt" => Layers, "forge" => Toolbox, "neoforge" => Fox, _ => Cube
    };
    public static string ForInstanceSection(string key) => key switch
    {
        "overview" => "M1,1 H9 V9 H1 Z M12,1 H20 V9 H12 Z M1,12 H9 V20 H1 Z M12,12 H20 V20 H12 Z",
        "settings" => Settings,
        "modify" => "M12,1 A6,6 0 0 0 7,9 L1,15 A3,3 0 0 0 5,19 L11,13 A6,6 0 0 0 19,5 L14,10 L10,6 L15,1 Z",
        "export" => Cube,
        "worlds" => Globe,
        "selection" => Folder,
        "screenshots" => "M2,2 H20 V20 H2 Z M2,17 L8,11 L12,15 L16,10 L20,14 M8,7 A2,2 0 1 1 4,7 A2,2 0 1 1 8,7",
        "mods" => Puzzle,
        "resourcepacks" => Layers,
        "shaders" => Sparkles,
        "schematics" => "M2,1 H18 V20 H2 Z M6,5 H14 V16 H6 Z M4,10 H16 M10,3 V18",
        "servers" => "M2,1 H18 V7 H2 Z M2,11 H18 V17 H2 Z M5,4 H6 M5,14 H6 M14,4 H16 M14,14 H16 M10,7 V11",
        "relations" => "M6,3 A3,3 0 1 1 0,3 A3,3 0 1 1 6,3 Z M20,12 A3,3 0 1 1 14,12 A3,3 0 1 1 20,12 Z M6,21 A3,3 0 1 1 0,21 A3,3 0 1 1 6,21 Z M6,3 H10 V12 H14 M10,12 V21 H6",
        _ => Cube
    };
}
