using System.Collections.Generic;
using System.Windows.Media;
using PCL.Core.App.IoC;

namespace PCL.Core.UI.Theme;

/// <summary>嘉然配色：社团 DESIGN.md 的语义颜色，深色版沿用玫瑰与可可色系。</summary>
public static class DianaPalette
{
    public static IReadOnlyDictionary<string, string> Colors(bool dark) => dark
        ? new Dictionary<string, string>
        {
            ["1"]="#F4D9BE", ["2"]="#FFB2BC", ["3"]="#E8A7B0", ["4"]="#D17D8A",
            ["5"]="#904855", ["6"]="#59343D", ["7"]="#402B31", ["8"]="#33262A",
            ["Gray1"]="#FDF9F3", ["Gray2"]="#F4D9BE", ["Gray3"]="#D8C1C3", ["Gray4"]="#C0A5A3",
            ["Gray5"]="#857274", ["Gray6"]="#534344", ["Gray7"]="#402F30", ["Gray8"]="#332728",
            ["White"]="#33262A", ["Background"]="#241D20", ["ToolTip"]="#402B31",
            ["Bg0"]="#59343D", ["Bg1"]="#DD33262A", ["TransparentBackground"]="#E6241D20",
            ["SemiWhite"]="#BB33262A", ["HalfWhite"]="#5533262A", ["Transparent"]="#0033262A",
            ["SemiTransparent"]="#0133262A", ["Memory"]="#FFB2BC"
        }
        : new Dictionary<string, string>
        {
            ["1"]="#5D453E", ["2"]="#904855", ["3"]="#A85868", ["4"]="#D17D8A",
            ["5"]="#E8A7B0", ["6"]="#FFD9DD", ["7"]="#FDEBED", ["8"]="#F9EFE4",
            ["Gray1"]="#1C1C18", ["Gray2"]="#534344", ["Gray3"]="#6F5B46", ["Gray4"]="#857274",
            ["Gray5"]="#B89D9E", ["Gray6"]="#D8C1C3", ["Gray7"]="#EBE8E2", ["Gray8"]="#F1EDE7",
            ["White"]="#FDF9F3", ["Background"]="#FDF9F3", ["ToolTip"]="#F9EFE4",
            ["Bg0"]="#E8A7B0", ["Bg1"]="#DDFDEBED", ["TransparentBackground"]="#E6FDF9F3",
            ["SemiWhite"]="#BBFDF9F3", ["HalfWhite"]="#55FDF9F3", ["Transparent"]="#00FDF9F3",
            ["SemiTransparent"]="#01FDEBED", ["Memory"]="#904855"
        };

    public static void Apply(bool dark)
    {
        var resources = Lifecycle.CurrentApplication.Resources;
        foreach (var (suffix, hex) in Colors(dark))
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            resources["ColorObject" + suffix] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources["ColorBrush" + suffix] = brush;
        }
    }
}
