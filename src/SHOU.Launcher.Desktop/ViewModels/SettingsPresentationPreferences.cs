using System.Text.Json;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

// Presentation preferences are isolated from credentials and from game launch configuration.
public sealed record SettingsPresentationPreferences(bool ShowClubIntroduction = true, bool ShowClubServers = true,
    bool RotateHomeBackground = true, decimal WindowOpacity = 100);

public sealed class SettingsPresentationStore(string dataDirectory)
{
    public string FilePath => SafePath.Within(dataDirectory, "desktop-presentation.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsPresentationPreferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 65536) throw new InvalidDataException("界面偏好文件过大，未加载。");
        var settings = JsonSerializer.Deserialize<SettingsPresentationPreferences>(File.ReadAllBytes(FilePath), Json)
            ?? throw new InvalidDataException("界面偏好文件为空，未加载。");
        Validate(settings);
        return settings;
    }

    public async Task SaveAsync(SettingsPresentationPreferences settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = SafePath.Within(dataDirectory, "desktop-presentation." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(settings, Json), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(SettingsPresentationPreferences settings)
    {
        if (settings.WindowOpacity is < 50 or > 100)
            throw new InvalidDataException("窗口不透明度须为 50–100%。");
    }
}
