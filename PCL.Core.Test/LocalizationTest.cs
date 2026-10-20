using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Localization;

namespace PCL.Core.Test;

[STATestClass]
public class LocalizationTest
{
    private static readonly string[] LanguageFiles = LocalizationService.SupportedLanguages
        .Select(language => language.Code)
        .ToArray();

    [STATestMethod]
    public void AllLanguageDictionariesShouldContainBaseKeys()
    {
        var baseKeys = LoadKeys("zh-CN");
        var app = Application.Current ?? new Application();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            foreach (var language in LanguageFiles)
            {
                // LocalizationService loads zh-CN first and overlays the selected language.
                // Check the resulting ResourceDictionary so sparse translations correctly use
                // the documented base-language fallback.
                LocalizationService.Apply(language, LocalizationService.FormatCultureFollowLanguage, false);
                var missing = baseKeys
                    .Where(key => app.TryFindResource(key) is not string value || string.IsNullOrWhiteSpace(value))
                    .ToArray();

                Assert.IsEmpty(missing, $"{language} 合并后的语言资源缺少键：{string.Join(", ", missing)}");
            }
        }
        finally
        {
            // Restore process-wide culture state changed by LocalizationService.Apply.
            LocalizationService.Apply(LocalizationService.DefaultLanguageCode, originalCulture.Name, false);
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            Lang.SyncCulture(originalCulture);
        }
    }


    [TestMethod]
    public void LanguageDictionariesShouldNotContainDuplicateKeys()
    {
        foreach (var language in LanguageFiles)
        {
            var keys = LoadKeyList(language);
            var duplicated = keys
                .GroupBy(key => key)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();

            Assert.IsEmpty(duplicated, $"{language} 存在重复语言键：{string.Join(", ", duplicated)}");
        }
    }

    [TestMethod]
    public void LanguageKeysShouldUseDotNaming()
    {
        foreach (var language in LanguageFiles)
        foreach (var key in LoadKeys(language))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(key), $"{language} 存在空语言键");
            Assert.IsTrue(key.Contains('.'), $"{language} 的语言键缺少分组分隔符：{key}");
            Assert.IsFalse(key.Contains(' '), $"{language} 的语言键不应包含空格：{key}");
        }
    }


    [TestMethod]
    public void SupportedLanguagesShouldHaveValidCultureAndResourceDictionary()
    {
        foreach (var language in LocalizationService.SupportedLanguages)
        {
            CultureInfo.GetCultureInfo(language.CultureName);

            var filePath = Path.Combine(GetRepositoryRoot(), "PCL.Core", "App", "Localization", "Languages",
                language.Code + ".xaml");
            Assert.IsTrue(File.Exists(filePath), $"{language.Code} 缺少语言资源文件");
        }
    }


    [TestMethod]
    public void FontProfileShouldFollowCultureGlyphStandard()
    {
        Assert.AreEqual(LocalizationFontProfile.SimplifiedChinese,
            LocalizationFontService.ResolveProfileFromCultureName("zh-CN"));
        Assert.AreEqual(LocalizationFontProfile.SimplifiedChinese,
            LocalizationFontService.ResolveProfileFromCultureName("zh-Hans"));
        Assert.AreEqual(LocalizationFontProfile.TraditionalChinese,
            LocalizationFontService.ResolveProfileFromCultureName("zh-TW"));
        Assert.AreEqual(LocalizationFontProfile.TraditionalChinese,
            LocalizationFontService.ResolveProfileFromCultureName("zh_HK"));
        Assert.AreEqual(LocalizationFontProfile.TraditionalChinese,
            LocalizationFontService.ResolveProfileFromCultureName("zh-Hant-HK"));
        Assert.AreEqual(LocalizationFontProfile.Japanese,
            LocalizationFontService.ResolveProfileFromCultureName("ja-JP"));
        Assert.AreEqual(LocalizationFontProfile.Korean,
            LocalizationFontService.ResolveProfileFromCultureName("ko-KR"));
        Assert.AreEqual(LocalizationFontProfile.English,
            LocalizationFontService.ResolveProfileFromCultureName("en-US"));
        Assert.AreEqual(LocalizationFontProfile.English,
            LocalizationFontService.ResolveProfileFromCultureName("en-GB"));
        Assert.AreEqual(LocalizationFontProfile.Other,
            LocalizationFontService.ResolveProfileFromCultureName("fr-FR"));
        Assert.AreEqual(LocalizationFontProfile.Other,
            LocalizationFontService.ResolveProfileFromCultureName("es-ES"));
        Assert.AreEqual(LocalizationFontProfile.Other,
            LocalizationFontService.ResolveProfileFromCultureName("pt-BR"));
    }

    [TestMethod]
    public void FontProfileAliasesShouldNotMakeLanguageResourceSupported()
    {
        Assert.IsFalse(LocalizationService.IsLanguageSupported("zh-HK"));
        Assert.AreEqual(LocalizationService.DefaultLanguageCode, LocalizationService.ResolveLanguage("zh-HK").Code);
    }

    [TestMethod]
    public void CustomFontShouldRemainFirstWhenPackFontIsUnavailable()
    {
        const string customFont = "Test Custom Font";

        var fontFamily = LocalizationFontService.BuildLaunchFontFamily(
            customFont,
            LocalizationService.ResolveLanguage("en-US"));

        Assert.IsTrue(fontFamily.ToString().StartsWith(customFont, StringComparison.Ordinal),
            $"自定义字体应保留在字体链首位，实际字体链：{fontFamily}");
    }

    private static HashSet<string> LoadKeys(string language)
    {
        return LoadKeyList(language).ToHashSet();
    }

    private static string[] LoadKeyList(string language)
    {
        var filePath = Path.Combine(GetRepositoryRoot(), "PCL.Core", "App", "Localization", "Languages",
            language + ".xaml");
        var document = XDocument.Load(filePath);
        var keyAttributeName = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        return document.Descendants()
            .Select(element => element.Attribute(keyAttributeName)?.Value)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToArray();
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "PCL.Core"))) return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("无法定位仓库根目录");
        return string.Empty;
    }
}
