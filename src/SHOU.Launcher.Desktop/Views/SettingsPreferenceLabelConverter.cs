using System.Globalization;
using Avalonia.Data.Converters;

namespace SHOU.Launcher.Desktop.Views;

public sealed class SettingsPreferenceLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString() switch
    {
        "zh-CN" => "简体中文（中国大陆）", "en-US" => "English (United States)", "system" => "跟随系统语言",
        "System" => "跟随系统", "Light" => "浅色", "Dark" => "深色", _ => value
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
