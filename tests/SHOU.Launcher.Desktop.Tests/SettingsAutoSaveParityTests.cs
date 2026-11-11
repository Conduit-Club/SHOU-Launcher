using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Localization;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class SettingsAutoSaveParityTests
{
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 300 && !done(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(done()); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task UiEditsCoalesceAndPersistWithoutSaveBarOrApplyButton()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show(); root.ShowSettingsCommand.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            Assert.Equal(0, service.SaveCalls);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is "SettingsSaveBar" or "SaveSettingsButton" or "DiscardSettingsButton");
            var field = Find<TextBox>(window, "CustomInfoSetting");
            field.Text = "first"; field.Text = "second"; field.Text = "last";
            await Until(() => !root.Settings.IsDirty);
            Assert.Equal("last", service.Snapshot.Settings.CustomInfo); Assert.Equal(1, service.SaveCalls);
            using var restarted = new MainWindowViewModel(service, new FakeExternalLauncher()); Assert.Equal("last", restarted.Settings.CustomInfo);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task SlowSaveUsesImmutableSnapshotAndSerializesNewerEdits()
    {
        var service = new DelayedSaveService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        root.Settings.MemoryMiB = 3072; root.Settings.LightAccent = "garnet";
        var first = root.Settings.FlushAutoSaveAsync(); await service.Entered.Task;
        root.Settings.MemoryMiB = 4096; root.Settings.LightAccent = "sky";
        service.Release.SetResult(); await first;
        Assert.Equal(3072, service.Snapshot.Settings.MemoryMiB); Assert.Equal("garnet", root.Settings.SavedPresentation.LightAccent);
        Assert.True(root.Settings.IsDirty); Assert.Equal(4096, root.Settings.MemoryMiB); Assert.Equal("sky", root.Settings.LightAccent);
        Assert.True(await root.Settings.FlushAutoSaveAsync());
        Assert.Equal(4096, service.Snapshot.Settings.MemoryMiB); Assert.Equal("sky", root.Settings.SavedPresentation.LightAccent);
        Assert.Equal(2, service.SaveCalls); Assert.Equal(1, service.MaximumConcurrent);
    }
    [AvaloniaFact]
    public async Task InvalidInputRetainsSavedConfigurationAndCorrectionAutosaves()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher()); var saved = service.Snapshot.Settings;
        root.Settings.GameRoot = "relative/path";
        await Until(() => root.Settings.HasAutoSaveFeedback);
        Assert.Equal(saved, service.Snapshot.Settings); Assert.Equal(0, service.SaveCalls); Assert.False(root.HasError);
        root.Settings.GameRoot = Path.Combine(service.Paths.InstancesDirectory, "new-root");
        await Until(() => !root.Settings.IsDirty);
        Assert.False(root.Settings.HasAutoSaveFeedback); Assert.Equal(root.Settings.GameRoot, service.Snapshot.Settings.GameRoot);
    }
    [AvaloniaFact]
    public async Task FailedWriteRetainsNewSecretAndLastSavedSettingsForRetry()
    {
        var service = new FailingSaveService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        root.Settings.Aria2Secret = "test-only-rpc-token"; root.Settings.MemoryMiB = 3072;
        Assert.False(await root.Settings.FlushAutoSaveAsync());
        Assert.True(root.Settings.HasAutoSaveFeedback); Assert.DoesNotContain("test-only-rpc-token", root.Settings.AutoSaveFeedback);
        Assert.Equal("test-only-rpc-token", root.Settings.Aria2Secret); Assert.Equal(2048, service.Snapshot.Settings.MemoryMiB);
        service.Fail = false; Assert.True(await root.Settings.FlushAutoSaveAsync());
        Assert.Equal("", root.Settings.Aria2Secret); Assert.NotNull(service.Snapshot.Settings.Aria2?.CredentialKey); Assert.Equal(3072, service.Snapshot.Settings.MemoryMiB);
    }
    [AvaloniaFact]
    public async Task ClassicPaletteBackgroundFontsAndMusicOptionsPersistAndApply()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        try
        {
            root.Settings.Theme = "Light"; root.Settings.LightAccent = "garnet"; root.Settings.DarkAccent = "death";
            root.Settings.MotdFont = "Arial"; root.Settings.BackgroundFit = "fit"; root.Settings.BackgroundBlur = 3;
            root.Settings.BackgroundOpacity = 73; root.Settings.SlideshowSeconds = 20;
            root.Settings.MusicVolume = 25; root.Settings.MusicShuffle = true; root.Settings.MusicGamePolicy = "stop";
            Assert.True(await root.Settings.FlushAutoSaveAsync()); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(Color.Parse("#A95B6B"), Assert.IsAssignableFrom<ISolidColorBrush>(Find<Border>(window, "LauncherTitleBar").Background).Color);
            Assert.Equal(Stretch.Uniform, Assert.IsType<ImageBrush>(root.Home.BackgroundBrush).Stretch);
            Assert.Equal(.73, root.Home.BackgroundImageOpacity); Assert.Equal(3, root.Home.BackgroundImageBlur); Assert.NotNull(root.Home.HomeBackground);
            var persisted = new SettingsPresentationStore(service.Paths.DataDirectory).Load();
            Assert.Equal("death", persisted.DarkAccent); Assert.Equal("Arial", persisted.MotdFontFamily); Assert.Equal(20, persisted.SlideshowSeconds);
            Assert.Equal(25, persisted.MusicVolume); Assert.True(persisted.MusicShuffle); Assert.Equal("stop", persisted.MusicGamePolicy);
            using var restarted = new MainWindowViewModel(service, new FakeExternalLauncher()); Assert.Equal(persisted, restarted.Settings.SavedPresentation);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task HiddenNavigationPersistsAndF12RevealRestoresAccessWithoutChangingPreferences()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        try
        {
            foreach (var choice in root.Settings.FeatureGroups.SelectMany(g => g.Choices).Where(c => c.Id is "settings" or "settings-about" or "instance-mods" or "mod-updates" or "feature-hide")) choice.Hidden = true;
            Assert.True(await root.Settings.FlushAutoSaveAsync()); Dispatcher.UIThread.RunJobs();
            Assert.False(Find<Avalonia.Controls.Primitives.ToggleButton>(window, "SettingsNavigationButton").IsVisible);
            Assert.DoesNotContain(root.Instances.ResourceSections, c => c.Key == "mods"); Assert.False(root.IsFeatureVisible("feature-hide"));
            var saved = root.Settings.SavedPresentation;
            root.ToggleHiddenFeatures(); Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Avalonia.Controls.Primitives.ToggleButton>(window, "SettingsNavigationButton").IsVisible);
            Assert.Contains(root.Instances.ResourceSections, c => c.Key == "mods"); Assert.True(root.IsFeatureVisible("feature-hide"));
            Assert.Equal(saved, root.Settings.SavedPresentation); Assert.False(root.Settings.IsDirty);
            root.ToggleHiddenFeatures(); Assert.False(root.IsFeatureVisible("settings"));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void OnlyThreeUiCatalogsShipAndLegacyLanguagesFallBackWhileRegionStaysIndependent()
    {
        Assert.Equal(new[] { "zh-CN", "zh-TW", "en-US" }, UiLocalization.SupportedLanguages);
        var resources = typeof(UiLocalization).Assembly.GetManifestResourceNames().Where(n => n.Contains("Localization.Resources.")).ToArray(); Assert.Equal(3, resources.Length);
        foreach (var legacy in new[] { "ja-JP", "fr-FR", "es-ES", "en-GB" }) Assert.Equal("en-US", UiLocalization.NormalizeLanguage(legacy));
        Assert.Equal("zh-TW", UiLocalization.NormalizeLanguage("zh-Hant")); Assert.Equal("zh-TW", UiLocalization.NormalizeLanguage("zh-HK")); Assert.Equal("zh-CN", UiLocalization.NormalizeLanguage("zh-SG"));
        var service = new FakeLauncherService(); service.Publish(service.Snapshot with { Settings = service.Snapshot.Settings with { Language = "ja-JP", Launcher = new(RegionalFormat: "fr-FR") } });
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        Assert.Equal("en-US", root.Settings.Language); Assert.Equal("fr-FR", root.Settings.RegionalFormat); Assert.Equal(3, root.Settings.Languages.Count);
    }
    [AvaloniaFact]
    public async Task AboutRestoresAttributedPeopleAndRoutesActionsThroughExternalLauncher()
    {
        var service = new FakeLauncherService(); var external = new FakeExternalLauncher(); using var root = new MainWindowViewModel(service, external);
        Assert.Equal(3, root.Settings.OriginalCredits.Count); Assert.All(root.Settings.OriginalCredits, c => Assert.True(c.HasPortrait));
        Assert.Equal(2, root.Settings.ClubAcknowledgements.Count); Assert.Equal(7, root.Settings.HistoricalCredits.Count);
        Assert.Contains(root.Settings.ClubContributors, c => c.Name == "Moeary"); Assert.Contains(root.Settings.ClubContributors, c => c.Name == "Codex");
        await root.Settings.OpenCommunitySourceCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/PCL-Community/PCL-CE", external.OpenedUris.Single().AbsoluteUri);
        Assert.NotEmpty(root.Settings.Licenses);
    }
    [AvaloniaFact]
    public async Task AppearanceAndAboutFitMinimumWindowInAllThreeLanguages()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = root }; window.Show(); root.ShowSettingsCommand.Execute(null);
        try
        {
            foreach (var language in UiLocalization.SupportedLanguages)
            {
                root.Settings.Language = language; Assert.True(await root.Settings.FlushAutoSaveAsync());
                foreach (var section in new[] { "appearance", "about", "language" })
                {
                    root.Settings.SelectedSection = section; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    var scroller = Find<ScrollViewer>(window, "SettingsPageScroller");
                    Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1, $"{language}/{section} overflows horizontally.");
                }
            }
            Assert.False(root.Settings.IsDirty);
        }
        finally { window.Close(); }
    }
    private sealed class DelayedSaveService : FakeLauncherService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int running; public int MaximumConcurrent { get; private set; }
        public override async Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default)
        { running++; MaximumConcurrent = Math.Max(MaximumConcurrent, running); try { if (!Entered.Task.IsCompleted) { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); } await base.SaveSettingsAsync(settings, cancellationToken); } finally { running--; } }
    }
    private sealed class FailingSaveService : FakeLauncherService
    {
        public bool Fail = true;
        public override Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => Fail ? throw new IOException("fixture write failure") : base.SaveSettingsAsync(settings, cancellationToken);
    }
}
