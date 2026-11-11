using System.Text.Json;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

// Presentation preferences are isolated from credentials and from game launch configuration.
public sealed record SettingsPresentationPreferences(bool ShowClubIntroduction = true, bool ShowClubServers = true,
    bool RotateHomeBackground = true, decimal WindowOpacity = 100, string[]? BackgroundImages = null,
    bool LockWindowSize = false, bool ShowStartupLogo = false, bool ShowLaunchTips = true, bool NotifyClubUpdates = false,
    bool AdvancedMaterial = false, string FontFamily = "", string LightAccent = "default", string DarkAccent = "default",
    string TitleMode = "default", string TitleText = "", string TitleImage = "", string HomeSource = "default",
    string HomeLocation = "", string[]? HiddenSections = null, string LastClubNotice = "",
    string MotdFontFamily = "", string BackgroundFit = "smart", decimal BackgroundOpacity = 100, decimal BackgroundBlur = 9, decimal SlideshowSeconds = 15, decimal MusicVolume = 50, bool MusicAutoPlay = false, bool MusicShuffle = false, string MusicGamePolicy = "keep");

public sealed class SettingsPresentationStore(string dataDirectory)
{
    public string FilePath => SafePath.Within(dataDirectory, "desktop-presentation.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsPresentationPreferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        SHOU.Launcher.Core.IO.SafePath.RejectLinks(FilePath);
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
            SHOU.Launcher.Core.IO.SafePath.RejectLinks(FilePath);
            SHOU.Launcher.Core.IO.SafePath.RejectLinks(FilePath + ".bak");
            var merged = File.Exists(FilePath) ? System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(FilePath, cancellationToken))!.AsObject() : new System.Text.Json.Nodes.JsonObject();
            foreach (var pair in JsonSerializer.SerializeToNode(settings, Json)!.AsObject()) merged[pair.Key] = pair.Value?.DeepClone();
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(merged, Json), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Validate(SettingsPresentationPreferences settings)
    {
        if (settings.WindowOpacity is < 50 or > 100)
            throw new InvalidDataException("窗口不透明度须为 50–100%。");
        if (settings.BackgroundImages is { Length: > 32 } || settings.BackgroundImages?.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) == true)
            throw new InvalidDataException("背景图片最多 32 张，须使用本机绝对路径。");
        if (settings.MotdFontFamily is null || settings.FontFamily is null || settings.TitleText is null || settings.TitleImage is null || settings.HomeLocation is null || settings.LastClubNotice is null
            || settings.MotdFontFamily.Length > 128 || settings.FontFamily.Length > 128 || settings.TitleText.Length > 256 || settings.TitleText.Any(char.IsControl)
            || settings.TitleMode is not ("default" or "none" or "text" or "image") || settings.HomeSource is not ("default" or "empty" or "local" or "remote")
            || settings.HiddenSections?.Any(s => !LauncherFeatures.IsKnown(s)) == true)
            throw new InvalidDataException("界面偏好格式无效。");
        if (settings.BackgroundFit is not ("smart" or "center" or "fit" or "stretch" or "tile" or "top-left" or "top-right" or "bottom-left" or "bottom-right") ||
            settings.BackgroundOpacity is < 0 or > 100 || settings.BackgroundBlur is < 0 or > 40 || settings.SlideshowSeconds is < 5 or > 300 || settings.MusicVolume is < 0 or > 100 || settings.MusicGamePolicy is not ("keep" or "stop" or "only"))
            throw new InvalidDataException("背景或音乐参数无效：透明度/音量 0–100%，模糊 0–40，轮播间隔 5–300 秒。");
    }
}
