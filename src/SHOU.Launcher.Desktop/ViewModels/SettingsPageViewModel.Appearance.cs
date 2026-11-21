namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class SettingsPageViewModel
{
    public string MotdFont { get => appearanceOptions.MotdFontFamily; set => Appearance(value ?? "", p => p.MotdFontFamily, (p, v) => p with { MotdFontFamily = v }); }
    public string BackgroundFit { get => appearanceOptions.BackgroundFit; set => Appearance(value, p => p.BackgroundFit, (p, v) => p with { BackgroundFit = v }); }
    public decimal BackgroundOpacity { get => appearanceOptions.BackgroundOpacity; set => Appearance(value, p => p.BackgroundOpacity, (p, v) => p with { BackgroundOpacity = v }); }
    public decimal BackgroundBlur { get => appearanceOptions.BackgroundBlur; set => Appearance(value, p => p.BackgroundBlur, (p, v) => p with { BackgroundBlur = v }); }
    public decimal SlideshowSeconds { get => appearanceOptions.SlideshowSeconds; set => Appearance(value, p => p.SlideshowSeconds, (p, v) => p with { SlideshowSeconds = v }); }
    public IReadOnlyList<string> BackgroundFits { get; } = ["smart", "center", "fit", "stretch", "tile", "top-left", "top-right", "bottom-left", "bottom-right"];
    public bool IsCustomHomeFile => HomeSource == "local";
    private void NotifyAppearanceFields()
    {
        foreach (var name in new[] { nameof(MotdFont), nameof(BackgroundFit), nameof(BackgroundOpacity), nameof(BackgroundBlur), nameof(SlideshowSeconds), nameof(IsCustomHomeFile), nameof(MusicVolume), nameof(MusicAutoPlay), nameof(MusicShuffle), nameof(MusicGamePolicy) }) OnPropertyChanged(name);
    }
}
