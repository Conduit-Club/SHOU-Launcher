using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL.Core.Test.Conduit;

[TestClass]
public sealed class CharacterPaletteTest
{
    [TestMethod]
    public void BothThemesHaveCompleteAndReadableLightAndDarkPalettes()
    {
        foreach (var theme in new[] { ColorTheme.Aimisi, ColorTheme.Elegant })
        foreach (var dark in new[] { false, true })
        {
            var colors = CharacterPalettes.Colors(theme, dark);
            CollectionAssert.AreEquivalent(DianaPalette.Colors(dark).Keys.ToArray(), colors.Keys.ToArray());
            foreach (var (text, surface) in new[] { ("Gray1", "Background"), ("1", "White"), ("2", "White"), ("3", "White"), ("Memory", "White") })
            {
                var a = Luminance(colors[text]);
                var b = Luminance(colors[surface]);
                Assert.IsGreaterThanOrEqualTo(4.5, (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05), $"{theme}/{dark}: {text}/{surface}");
            }
        }
    }

    private static double Luminance(string color)
    {
        var c = Enumerable.Range(0, 3).Select(i => Convert.ToInt32(color.Substring(1 + i * 2, 2), 16) / 255d)
            .Select(v => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)).ToArray();
        return c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
    }
}
