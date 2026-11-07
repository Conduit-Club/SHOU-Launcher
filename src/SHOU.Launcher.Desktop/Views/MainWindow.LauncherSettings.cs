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
    private void LauncherRootChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainWindowViewModel.Snapshot)) ApplyLauncherPreferences(); }
    private void ApplyLauncherPreferences()
    {
        if (launcherSettingsOwner is not { } vm) return; var p = vm.Settings.SavedPresentation;
        CanResize = !p.LockWindowSize; CanMaximize = !p.LockWindowSize;
        if (Application.Current is { } app)
        {
            app.Resources["ClubFontFamily"] = new FontFamily(string.IsNullOrWhiteSpace(p.FontFamily) ? OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC" : p.FontFamily);
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
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("DownloadsNavigationButton")!.IsVisible = p.HiddenSections?.Contains("download") != true;
        this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ToolsNavigationButton")!.IsVisible = p.HiddenSections?.Contains("tools") != true;
    }
    private static void ApplyAccent(Application app, SettingsPresentationPreferences p, bool dark)
    {
        var choice = dark ? p.DarkAccent : p.LightAccent;
        var accent = choice switch { "garnet" => dark ? "#F2A7AC" : "#A45160", "blue" => dark ? "#8FD2F1" : "#17658B", "jade" => dark ? "#8BD7C5" : "#226D60", _ => dark ? "#EF9CC8" : "#AC2F6C" };
        var title = choice switch { "garnet" => dark ? "#6F3543" : "#AB5361", "blue" => dark ? "#274C69" : "#327A9A", "jade" => dark ? "#28594F" : "#367968", _ => dark ? "#74365F" : "#B14F91" };
        app.Resources["ClubAccent"] = new SolidColorBrush(Color.Parse(accent)); app.Resources["ClubTitleBar"] = new SolidColorBrush(Color.Parse(title));
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
