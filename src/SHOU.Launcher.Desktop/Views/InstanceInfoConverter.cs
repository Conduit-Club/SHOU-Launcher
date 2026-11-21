using System.Globalization;
using Avalonia.Data.Converters;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.Views;

public sealed class InstanceInfoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GameInstance instance) return "";
        if (parameter as string == "loader") return instance.Loader == "vanilla" ? "" : instance.Loader switch { "fabric" => "Fabric", "quilt" => "Quilt", "forge" => "Forge", "neoforge" => "NeoForge", "cleanroom" => "Cleanroom", "legacyfabric" => "Legacy Fabric", "optifine" => "OptiFine", "labymod" => "LabyMod", "liteloader" => "LiteLoader", _ => instance.Loader } + " " + instance.LoaderVersion;
        try { return InstanceIdentity.GameVersion(instance); } catch (InvalidDataException) { return instance.VersionId; }
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
