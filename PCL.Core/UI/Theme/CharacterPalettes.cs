using System;
using System.Collections.Generic;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>参考用户角色配色的语义色板；亮暗模式均提供独立的正文、表面和强调色。</summary>
public static class CharacterPalettes
{
    public static IReadOnlyDictionary<string, string> Colors(ColorTheme theme, bool dark)
    {
        string[] colors;
        string[] grays;
        string white, background, memory;
        switch (theme, dark)
        {
            case (ColorTheme.Aimisi, false):
                colors = ["#56334C", "#99345F", "#286F96", "#C7538A", "#EB9BBE", "#F6C5DC", "#FCE5F0", "#EDF7FD"];
                grays = ["#302633", "#50434F", "#6D5B69", "#887580", "#AC99A5", "#D9CBD4", "#EBE5EC", "#F6F0F5"];
                white = "#FFFBFD"; background = "#FFF7FB"; memory = "#286F96";
                break;
            case (ColorTheme.Aimisi, true):
                colors = ["#FFE7F4", "#FFACD3", "#99DDF6", "#E47EB1", "#92486F", "#60354E", "#402939", "#292B3A"];
                grays = ["#F9EDF5", "#E5CEDD", "#CBB6C5", "#AE94A5", "#857080", "#574452", "#3D303B", "#302631"];
                white = "#302330"; background = "#211D29"; memory = "#99DDF6";
                break;
            case (ColorTheme.Elegant, false):
                colors = ["#22262B", "#745700", "#5E6369", "#806000", "#E6C34D", "#F4E4A2", "#FAF4DE", "#F1F3F5"];
                grays = ["#20242A", "#343A42", "#545B64", "#747C86", "#9BA2AB", "#CFD4DA", "#E6E9ED", "#F2F4F6"];
                white = "#FFFFFF"; background = "#F4F5F7"; memory = "#745700";
                break;
            case (ColorTheme.Elegant, true):
                colors = ["#F6F7F8", "#F4D16B", "#CFD5DE", "#D9B44A", "#8D7742", "#50482D", "#35332D", "#292C31"];
                grays = ["#F5F6F7", "#DCE0E5", "#BEC5CE", "#9BA5B1", "#707A86", "#444B55", "#30363E", "#252A31"];
                white = "#252A31"; background = "#191D23"; memory = "#F4D16B";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(theme));
        }
        var result = new Dictionary<string, string>();
        for (var i = 0; i < 8; i++) { result[(i + 1).ToString()] = colors[i]; result["Gray" + (i + 1)] = grays[i]; }
        result["White"] = white;
        result["Background"] = background;
        result["ToolTip"] = colors[7];
        result["Bg0"] = colors[4];
        result["Bg1"] = "#DD" + colors[7][1..];
        result["TransparentBackground"] = "#E6" + background[1..];
        result["SemiWhite"] = "#BB" + white[1..];
        result["HalfWhite"] = "#55" + white[1..];
        result["Transparent"] = "#00" + white[1..];
        result["SemiTransparent"] = "#01" + colors[7][1..];
        result["Memory"] = memory;
        return result;
    }

    public static void Apply(ColorTheme theme, bool dark)
    {
        var resources = Lifecycle.CurrentApplication.Resources;
        foreach (var (suffix, hex) in Colors(theme, dark))
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            resources["ColorObject" + suffix] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources["ColorBrush" + suffix] = brush;
        }
    }
}
