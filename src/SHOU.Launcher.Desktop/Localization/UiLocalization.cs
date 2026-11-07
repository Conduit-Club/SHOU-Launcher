using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SHOU.Launcher.Desktop.Localization;

public sealed class UiLocalization : ObservableObject
{
    public static UiLocalization Instance { get; } = new();
    private static readonly CultureInfo SystemUi = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo SystemRegion = CultureInfo.CurrentCulture;
    private readonly Dictionary<string, Dictionary<string, string>> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private string locale = "zh-CN";
    private int revision;
    public int Revision => revision;
    public string Locale => locale;
    public static string Key(string phrase) => "t" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(phrase)))[..16];
    private Dictionary<string, string> Catalog(string code)
    {
        if (catalogs.TryGetValue(code, out var catalog)) return catalog;
        using var data = typeof(UiLocalization).Assembly.GetManifestResourceStream("SHOU.Launcher.Desktop.Localization.Resources." + code + ".json");
        catalog = data is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? [];
        catalogs[code] = catalog; return catalog;
    }
    public void SetLanguage(string requested, string region = "system")
    {
        var code = requested == "system" ? SystemUi.Name : requested;
        var supported = new[] { "zh-CN", "zh-TW", "en-US", "en-GB", "ja-JP", "fr-FR", "es-ES" };
        code = supported.Contains(code, StringComparer.OrdinalIgnoreCase) ? supported.First(c => c.Equals(code, StringComparison.OrdinalIgnoreCase))
            : code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : supported.FirstOrDefault(c => c.StartsWith(code.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase)) ?? "en-US";
        // Do not pin an AsyncLocal culture in one save callback: it would revert at the await boundary.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentCulture = region == "system" ? SystemRegion : CultureInfo.GetCultureInfo(region);
        if (locale == code) return; locale = code; revision++; OnPropertyChanged(nameof(Revision)); OnPropertyChanged(nameof(Locale));
    }
    public string Get(string key)
    {
        if (locale == "zh-CN") return Catalog("zh-CN").GetValueOrDefault(key, key);
        return Catalog(locale).GetValueOrDefault(key) ?? Catalog("en-US").GetValueOrDefault(key) ?? Catalog("zh-CN").GetValueOrDefault(key, key);
    }
    public string Text(string phrase)
    {
        if (string.IsNullOrEmpty(phrase) || locale == "zh-CN") return phrase;
        // Picker labels are native language names, not translatable UI captions.
        if (phrase is "简体中文（中国大陆）" or "繁體中文（台灣）" or "English (United States)" or "English (United Kingdom)" or "日本語" or "Français" or "Español") return phrase;
        var key = Key(phrase); return Catalog("zh-CN").ContainsKey(key) ? Get(key) : phrase;
    }
    public string Format(string phrase, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(phrase), arguments);
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(UiLocalization.Revision))
    { Source = UiLocalization.Instance, Converter = new LiteralConverter(key) };
    private sealed class LiteralConverter(string key) : IValueConverter
    {
        public object Convert(object? value, Type type, object? parameter, CultureInfo culture) => UiLocalization.Instance.Get(key);
        public object ConvertBack(object? value, Type type, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class TranslateExtension(string path) : MarkupExtension
{
    public IValueConverter? Converter { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Bindings = { new Binding(path), new Binding(nameof(UiLocalization.Revision)) { Source = UiLocalization.Instance } },
        Converter = new TranslatedValue(Converter)
    };
    private sealed class TranslatedValue(IValueConverter? converter) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var original = values.Count == 0 ? null : values[0];
            if (converter is not null) original = converter.Convert(original, typeof(string), null, culture);
            return original is string text ? UiLocalization.Instance.Text(text) : original;
        }
    }
}
