using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private MainWindowViewModel? launcherSettingsOwner;
    private Bitmap? customBrand;
    private bool startupPreferencesApplied;
    private void InitializeLauncherPreferences()
    {
        ActualThemeVariantChanged += (_, _) => ApplyLauncherPreferences();
        DataContextChanged += (_, _) =>
        {
            if (launcherSettingsOwner is { } previous) { previous.Settings.PropertyChanged -= LauncherPreferencesChanged; previous.PropertyChanged -= LauncherRootChanged; }
            launcherSettingsOwner = DataContext as MainWindowViewModel;
            if (launcherSettingsOwner is { } next) { next.Settings.PropertyChanged += LauncherPreferencesChanged; next.PropertyChanged += LauncherRootChanged; ApplyLauncherPreferences(); }
        };
        Closed += (_, _) =>
        {
            if (launcherSettingsOwner is { } vm) { vm.Settings.PropertyChanged -= LauncherPreferencesChanged; vm.PropertyChanged -= LauncherRootChanged; }
            customBrand?.Dispose();
        };
        Opened += async (_, _) =>
        {
            if (startupPreferencesApplied || launcherSettingsOwner is not { } vm) return; startupPreferencesApplied = true;
            var p = vm.Settings.SavedPresentation;
            if (p.ShowStartupLogo) { this.FindControl<Border>("StartupLogoOverlay")!.IsVisible = true; await Task.Delay(700); this.FindControl<Border>("StartupLogoOverlay")!.IsVisible = false; }
            if (p.NotifyClubUpdates) await CheckStartupClubNotice(vm);
        };
    }
    private void LauncherPreferencesChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SettingsPageViewModel.SavedPresentation)) ApplyLauncherPreferences(); }
    private void LauncherRootChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(MainWindowViewModel.Snapshot) or nameof(MainWindowViewModel.RevealHiddenFeatures)) ApplyLauncherPreferences(); }
    private void ApplyLauncherPreferences()
    {
        if (launcherSettingsOwner is not { } vm) return; var p = vm.Settings.SavedPresentation;
        CanResize = !p.LockWindowSize; CanMaximize = !p.LockWindowSize;
        if (Application.Current is { } app)
        {
            app.Resources["ClubFontFamily"] = new FontFamily(string.IsNullOrWhiteSpace(p.FontFamily) ? OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC" : p.FontFamily);
            app.Resources["ClubMotdFontFamily"] = new FontFamily(string.IsNullOrWhiteSpace(p.MotdFontFamily) ? string.IsNullOrWhiteSpace(p.FontFamily) ? OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC" : p.FontFamily : p.MotdFontFamily);
            ApplyAccent(app, p, ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        }
        TransparencyLevelHint = p.AdvancedMaterial ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.Transparent];
        this.FindControl<Border>("WindowSurface")!.Opacity = p.AdvancedMaterial ? .96 : 1;
        var label = this.FindControl<TextBlock>("WindowBrandTitle")!;
        label.IsVisible = p.TitleMode is "default" or "text";
        this.FindControl<Border>("ShouBadge")!.IsVisible = p.TitleMode == "default";
        this.FindControl<Image>("BrandLogo")!.IsVisible = p.TitleMode != "none";
        var custom = this.FindControl<Image>("CustomBrandImage")!; custom.IsVisible = p.TitleMode == "image";
        if (p.TitleMode == "image" && File.Exists(p.TitleImage))
        {
            try { SHOU.Launcher.Core.IO.SafePath.RejectLinks(p.TitleImage); if (new FileInfo(p.TitleImage).Length > 5 * 1024 * 1024) return; using var file = File.OpenRead(p.TitleImage); var old = customBrand; customBrand = Bitmap.DecodeToHeight(file, 64); custom.Source = customBrand; old?.Dispose(); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { custom.IsVisible = false; }
        }
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("DownloadsNavigationButton")!.IsVisible = vm.IsFeatureVisible("download");
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ToolsNavigationButton")!.IsVisible = vm.IsFeatureVisible("tools");
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SettingsNavigationButton")!.IsVisible = vm.IsFeatureVisible("settings");
    }
    private string? lastAccentKey;
    private void ApplyAccent(Application app, SettingsPresentationPreferences p, bool dark)
    {
        var key = p.LightAccent + "/" + p.DarkAccent;
        if (lastAccentKey == key) return; lastAccentKey = key;
        foreach (var variant in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            var isDark = variant == Avalonia.Styling.ThemeVariant.Dark;
            var choice = isDark ? p.DarkAccent : p.LightAccent;
            var palette = Themes.LauncherAccentPalette.For(choice, isDark);
            var resources = new ResourceDictionary();
            foreach (var (name, value) in new[] { ("ClubBackground", palette.Background), ("ClubSurface", palette.Surface),
                ("ClubSidebar", palette.Sidebar), ("ClubForeground", palette.Foreground), ("ClubMuted", palette.Muted),
                ("ClubAccent", palette.Accent), ("ClubTitleBar", palette.Title), ("ClubBorder", palette.Border),
                ("ClubInputBorder", palette.Border), ("ClubSelection", palette.Selection), ("ClubSelectionForeground", palette.SelectionForeground),
                ("ClubTitleSelectionForeground", Themes.LauncherAccentPalette.For(choice, false).SelectionForeground), ("ClubHover", palette.Selection) })
                resources[name] = new SolidColorBrush(Color.Parse(value));
            resources["ClubPageGradient"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops { new(Color.Parse(palette.Background), 0), new(Color.Parse(palette.Surface), 1) }
            };
            app.Resources.ThemeDictionaries[variant] = resources;
        }
    }

    private async Task CheckStartupClubNotice(MainWindowViewModel vm)
    {
        try
        {
            if (vm.Service is not SHOU.Launcher.Core.Abstractions.IClubContentService content) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var history = await content.GetClubUpdatesAsync(false, timeout.Token);
            var latest = history.Notices.FirstOrDefault(); if (latest is null) return;
            var key = latest.DateText + latest.Title; if (vm.Settings.SavedPresentation.LastClubNotice == key) return;
            if (!ReferenceEquals(vm.SelectedPage, vm.Home)) return;
            await vm.Home.OpenReleasesCommand.ExecuteAsync(null);
            await vm.Settings.RecordClubNoticeAsync(key, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
    }
}
