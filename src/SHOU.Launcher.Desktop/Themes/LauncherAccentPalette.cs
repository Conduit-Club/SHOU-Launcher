namespace SHOU.Launcher.Desktop.Themes;

// Semantic Avalonia palettes. Classic names follow the WPF choices; legacy IDs remain readable.
internal sealed record LauncherAccentPalette(string Background, string Surface, string Sidebar, string Foreground,
    string Muted, string Accent, string Title, string Border, string Selection, string SelectionForeground)
{
    internal static LauncherAccentPalette For(string choice, bool dark) => (choice, dark) switch
    {
        ("garnet", false) => new("#FDF9F3", "#FCF8F1", "#FEFAF4", "#55483F", "#928078", "#A45160", "#A95B6B", "#E6D9D0", "#FCE7EA", "#904855"),
        ("garnet", true) => new("#241D20", "#33262A", "#2D2226", "#F4D9BE", "#C0A5A3", "#FFB2BC", "#59343D", "#534344", "#402B31", "#FFB2BC"),
        ("aimisi", false) => new("#FFF7FB", "#FFFBFD", "#FCE5F0", "#302633", "#887580", "#99345F", "#C7538A", "#D9CBD4", "#EDF7FD", "#286F96"),
        ("aimisi", true) => new("#211D29", "#302330", "#292B3A", "#FFE7F4", "#AE94A5", "#FFACD3", "#60354E", "#574452", "#402939", "#99DDF6"),
        ("elegant", false) => new("#F4F5F7", "#FFFFFF", "#F1F3F5", "#22262B", "#747C86", "#745700", "#806000", "#CFD4DA", "#FAF4DE", "#745700"),
        ("elegant", true) => new("#191D23", "#252A31", "#20242A", "#F6F7F8", "#9BA5B1", "#F4D16B", "#50482D", "#444B55", "#35332D", "#F4D16B"),
        ("sky" or "blue", false) => new("#F2F9FD", "#FBFDFF", "#F5FAFE", "#29485D", "#6C879A", "#17658B", "#327A9A", "#D7E6EF", "#DFEFFA", "#17658B"),
        ("sky" or "blue", true) => new("#182831", "#223640", "#1C2F39", "#DDEEF5", "#A3BECF", "#8FD2F1", "#274C69", "#3A5364", "#294A5D", "#8FD2F1"),
        ("cat", false) => new("#F4F6FD", "#FBFCFF", "#F0F3FC", "#354365", "#7B85A2", "#4B69A5", "#5A77AC", "#DCE1ED", "#E8EDFC", "#4B69A5"),
        ("cat", true) => new("#202532", "#2A3040", "#242B39", "#E3E8F7", "#ACB7D1", "#A9C5FF", "#3F5175", "#46506A", "#334362", "#A9C5FF"),
        ("death", false) => new("#F0F3FC", "#F9FBFF", "#EBF0FD", "#293962", "#6C7FA7", "#234FC2", "#2E55B2", "#D2DCF2", "#DDE7FF", "#234FC2"),
        ("death", true) => new("#151D31", "#212D47", "#19233A", "#DFE7FF", "#9FAFD2", "#A0BEFF", "#263C6F", "#3B4D75", "#2B4170", "#A0BEFF"),
        ("jade", false) => new("#F0FAF5", "#FAFFFC", "#F1F9F6", "#2F5148", "#6B8B80", "#226D60", "#367968", "#D7E7DF", "#E1F1E9", "#226D60"),
        ("jade", true) => new("#1C2C26", "#273B32", "#22332C", "#DCEFE5", "#ABC7B9", "#8BD7C5", "#28594F", "#3C5749", "#2C4B3B", "#8BD7C5"),
        (_, false) => new("#FFF5FB", "#FCF6FB", "#FFF9FC", "#5C3A54", "#927B8B", "#AC2F6C", "#B14F91", "#EADDE6", "#EDF8FE", "#26799F"),
        _ => new("#261F2A", "#302634", "#2B222F", "#F0DDEB", "#BEA8B9", "#EF9CC8", "#74365F", "#49384A", "#233B49", "#8FD2F1")
    };
}
