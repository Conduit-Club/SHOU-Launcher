using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class HomePageViewModel
{
    private SettingsPresentationPreferences presentation = new();
    private readonly DispatcherTimer backgroundTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Bitmap? background, previousBackground;
    private double backgroundFade = 1;
    public double BackgroundFade { get => backgroundFade; private set => SetProperty(ref backgroundFade, value); }
    public bool HasHomeBackdrop => presentation.RotateHomeBackground || (presentation.BackgroundImages?.Length ?? 0) > 0;
    public double BackgroundImageOpacity => (double)presentation.BackgroundOpacity / 100;
    public double BackgroundImageBlur => (double)presentation.BackgroundBlur;
    public IBrush? BackgroundBrush => Brush(background);
    public IBrush? PreviousBackgroundBrush => Brush(previousBackground);
    private IBrush? Brush(Bitmap? bitmap)
    {
        if (bitmap is null) return null;
        var fit = presentation.BackgroundFit;
        return new ImageBrush(bitmap)
        {
            Stretch = fit is "smart" ? Stretch.UniformToFill : fit == "fit" ? Stretch.Uniform : fit == "stretch" ? Stretch.Fill : Stretch.None,
            AlignmentX = fit.EndsWith("left", StringComparison.Ordinal) ? AlignmentX.Left : fit.EndsWith("right", StringComparison.Ordinal) ? AlignmentX.Right : AlignmentX.Center,
            AlignmentY = fit.StartsWith("top", StringComparison.Ordinal) ? AlignmentY.Top : fit.StartsWith("bottom", StringComparison.Ordinal) ? AlignmentY.Bottom : AlignmentY.Center,
            TileMode = fit == "tile" ? TileMode.Tile : TileMode.None,
            DestinationRect = fit == "tile" ? new RelativeRect(0, 0, bitmap.Size.Width, bitmap.Size.Height, RelativeUnit.Absolute) : RelativeRect.Fill
        };
    }
    private string[] backgroundFiles = [];
    private int backgroundIndex;
    private bool backgroundTimerReady;
    private string homeDocument = "";
    private CancellationTokenSource? customHomeRead;
    private long homeGeneration;
    public Bitmap? HomeBackground => background;
    public bool IsCustomHome => presentation.HomeSource != "default";
    public bool ShowDefaultHome => !IsCustomHome;
    public string HomeDocumentLocation => presentation.HomeLocation;
    public bool ShowCustomHomeDocument => IsCustomHome && presentation.HomeSource != "empty";
    public bool IsRemoteHome => presentation.HomeSource == "remote";
    public string HomeDocument { get => homeDocument; private set => SetProperty(ref homeDocument, value); }
    public bool ShowGameTip => presentation.ShowLaunchTips;
    public string GameTip => "你可以在实例设置中为每个实例选择独立的 Java 和内存。";
    internal void ApplyAppearance(SettingsPresentationPreferences settings)
    {
        var previousHome = presentation.HomeSource + presentation.HomeLocation;
        var previousImages = string.Join('\n', presentation.BackgroundImages ?? []);
        presentation = settings;
        if (background is null || previousImages != string.Join('\n', settings.BackgroundImages ?? [])) RefreshBackgrounds();
        backgroundTimer.Interval = TimeSpan.FromSeconds((double)settings.SlideshowSeconds);
        backgroundTimer.IsEnabled = settings.RotateHomeBackground && backgroundFiles.Length > 1;
        foreach (var field in new[] { nameof(HasHomeBackdrop), nameof(BackgroundBrush), nameof(PreviousBackgroundBrush), nameof(BackgroundImageOpacity), nameof(BackgroundImageBlur) }) OnPropertyChanged(field);
        foreach (var name in new[] { nameof(IsCustomHome), nameof(ShowCustomHomeDocument), nameof(ShowDefaultHome), nameof(HomeDocumentLocation), nameof(IsRemoteHome), nameof(ShowGameTip), nameof(ShowClubIntroduction), nameof(ShowClubServers) }) OnPropertyChanged(name);
        if (previousHome != settings.HomeSource + settings.HomeLocation) _ = ReadCustomHomeAsync();
    }
    public void RefreshBackgrounds()
    {
        if (!backgroundTimerReady) { backgroundTimerReady = true; backgroundTimer.Tick += (_, _) => { backgroundIndex++; LoadBackground(); }; }
        backgroundFiles = (presentation.BackgroundImages ?? []).Where(File.Exists).Take(32).ToArray();
        if (backgroundFiles.Length == 0) backgroundFiles = ["avares://SHOU.Launcher.Desktop/Assets/Conduit/campus-lake.jpg", "avares://SHOU.Launcher.Desktop/Assets/Conduit/spawn-plaza.jpg", "avares://SHOU.Launcher.Desktop/Assets/Conduit/spawn-statues.jpg"];
        backgroundIndex = 0; LoadBackground();
        backgroundTimer.IsEnabled = presentation.RotateHomeBackground && backgroundFiles.Length > 1;
    }
    private void LoadBackground()
    {
        Bitmap? next = null;
        try
        {
            if (backgroundFiles.Length > 0)
            {
                var path = backgroundFiles[backgroundIndex % backgroundFiles.Length];
                if (path.StartsWith("avares://", StringComparison.Ordinal))
                { using var input = AssetLoader.Open(new Uri(path)); next = Bitmap.DecodeToWidth(input, 1600); }
                else
                {
                    SHOU.Launcher.Core.IO.SafePath.RejectLinks(path);
                    if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("背景图片过大。");
                    using var input = File.OpenRead(path); next = Bitmap.DecodeToWidth(input, 1600);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        if (next is null)
        {
            try { using var input = AssetLoader.Open(new Uri("avares://SHOU.Launcher.Desktop/Assets/Conduit/campus-lake.jpg")); next = Bitmap.DecodeToWidth(input, 1600); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { }
        }
        previousBackground?.Dispose(); previousBackground = background; background = next;
        BackgroundFade = previousBackground is null ? 1 : 0;
        foreach (var field in new[] { nameof(HomeBackground), nameof(BackgroundBrush), nameof(PreviousBackgroundBrush) }) OnPropertyChanged(field);
        Dispatcher.UIThread.Post(() => BackgroundFade = 1);
    }
    public CommunityToolkit.Mvvm.Input.AsyncRelayCommand RefreshCustomHomeCommand => refreshCustomHomeCommand ??= new(ReadCustomHomeAsync);
    private CommunityToolkit.Mvvm.Input.AsyncRelayCommand? refreshCustomHomeCommand;
    private async Task ReadCustomHomeAsync()
    {
        customHomeRead?.Cancel(); var generation = ++homeGeneration; HomeDocument = "";
        if (!IsCustomHome || presentation.HomeSource == "empty" || Service is not ILauncherInformationService info) { HomeDocument = ""; return; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(communityLifetime.Token); customHomeRead = cts;
        try { var text = await info.ReadHomeDocumentAsync(presentation.HomeLocation, presentation.HomeSource == "remote", cts.Token); if (generation == homeGeneration && !cts.IsCancellationRequested) HomeDocument = text; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or UnauthorizedAccessException)
        { if (generation == homeGeneration) HomeDocument = "主页读取失败：" + UiPrivacy.Redact(ex.Message); }
        finally { if (ReferenceEquals(customHomeRead, cts)) customHomeRead = null; }
    }
    private void DisposeAppearance() { customHomeRead?.Cancel(); backgroundTimer.Stop(); background?.Dispose(); previousBackground?.Dispose(); }
}
