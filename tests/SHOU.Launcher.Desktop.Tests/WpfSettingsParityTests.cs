using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Core.Conduit;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfSettingsParityTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void ContextNavigationHasReferenceWidthAndRetainsDraftAcrossEveryCategory()
    {
        using var root = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new Window { Width = 976, Height = 588, Content = new SettingsView { DataContext = root.Settings } };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(135, Find<Border>(window, "SettingsContextNavigation").Bounds.Width);
            Assert.InRange(Find<Button>(window, "SettingsLaunchNavigation").Bounds.Width, 130, 135);
            Assert.Equal(841, Find<ScrollViewer>(window, "SettingsPageScroller").Bounds.Width);
            Assert.InRange(Find<Border>(window, "SettingsSaveBar").Bounds.Height, 28, 36);
            var memory = Find<NumericUpDown>(window, "MaximumMemorySetting");
            Assert.True(memory.Bounds.Width >= 130);
            Assert.Equal("2048", memory.GetVisualDescendants().OfType<TextBox>().Single().Text);
            root.Settings.CustomInfo = "explicit parity draft";
            foreach (var section in SettingsPageViewModel.SectionIds)
            {
                root.Settings.SelectSectionCommand.Execute(section); Pump(window);
                Assert.Equal(section, root.Settings.SelectedSection); Assert.True(root.Settings.IsDirty);
                Assert.Equal("explicit parity draft", root.Settings.CustomInfo);
            }
            Assert.Equal(11, window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("contextNavigation")));
            Assert.False(((SettingsView)window.Content!).FindControl<TextBox>("UnsupportedJvmArgumentsSetting")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowAndMinimumMemoryDraftValidateAndRoundTripThroughService()
    {
        var service = new FakeLauncherService();
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var settings = root.Settings;
        settings.UseCustomWindow = true; settings.GameWindowWidth = 1280; settings.GameWindowHeight = 720;
        settings.GameFullscreen = true; settings.UseMinimumMemory = true; settings.MinimumMemoryMiB = 512;
        settings.CustomInfo = "club parity fixture";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1280, service.Snapshot.Settings.GameWindowWidth); Assert.Equal(720, service.Snapshot.Settings.GameWindowHeight);
        Assert.True(service.Snapshot.Settings.GameFullscreen); Assert.Equal(512, service.Snapshot.Settings.MinimumMemoryMiB);
        Assert.Equal("club parity fixture", service.Snapshot.Settings.CustomInfo); Assert.False(settings.IsDirty);
        settings.MinimumMemoryMiB = 4096;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, service.SaveCalls); Assert.True(settings.IsDirty); Assert.Contains("初始内存", root.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task SourceReorderingRetainsSelectionAndDoesNotPersistUntilSave()
    {
        var service = new FakeLauncherService();
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var settings = root.Settings;
        Assert.True(settings.Sources.Count >= 2);
        var selected = settings.Sources[1]; settings.SelectedSource = selected;
        settings.MoveSourceUpCommand.Execute(null);
        Assert.Same(selected, settings.SelectedSource); Assert.Same(selected, settings.Sources[0]); Assert.Equal(0, service.SaveCalls);
        settings.SelectedSourceCategory = SourceCategory.Mod; settings.SelectedSourceCategory = SourceCategory.Minecraft;
        Assert.Same(selected, settings.Sources[0]);
        await settings.SaveCommand.ExecuteAsync(null);
        var saved = DownloadSources.Parse(service.Snapshot.Settings.DownloadSourcesJson);
        Assert.Equal(selected.Name, saved.Where(s => s.Category == SourceCategory.Minecraft).OrderBy(s => s.Priority).First().Name);
        Assert.False(settings.IsDirty);
    }

    [AvaloniaFact]
    public async Task FreshHomeShowsClubCardsAndExplicitlySavedHiddenCardsSurviveReload()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Guarded test root required."), "test-data/wpf-parity/r6-home-defaults", Guid.NewGuid().ToString("N"));
        try
        {
            var network = new RejectPresentationNetwork();
            using var http = new HttpClient(network);
            var downloads = new VerifiedDownloader(http);
            var platform = PlatformServicesFactory.Create(new(directory), downloads);
            using var lease = platform.DataLock.Acquire(platform.Paths);
            await using var processes = (ProcessRunner)platform.Processes;
            await using var service = new LauncherService(platform, http, downloads, new NetworkOptions());
            await service.InitializeAsync();
            var store = new SettingsPresentationStore(service.Paths.DataDirectory);
            Assert.False(File.Exists(store.FilePath));
            AssertHomeCards(true, true);
            Assert.False(File.Exists(store.FilePath));

            foreach (var (json, introduction, servers) in new[]
            {
                ("{}", true, true),
                ("{\"showClubIntroduction\":false}", false, true),
                ("{\"showClubServers\":false}", true, false)
            })
            {
                await File.WriteAllTextAsync(store.FilePath, json);
                AssertHomeCards(introduction, servers);
                Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath));
            }

            await File.WriteAllTextAsync(store.FilePath, "{}");
            using (var owner = new MainWindowViewModel(service, new FakeExternalLauncher()))
            {
                var window = new MainWindow { DataContext = owner }; window.Show(); Pump(window);
                try
                {
                    Assert.True(Find<Border>(window, "ClubIntroductionCard").IsEffectivelyVisible);
                    Assert.True(Find<Border>(window, "ClubServersCard").IsEffectivelyVisible);
                    owner.Settings.ShowClubIntroduction = false; owner.Settings.ShowClubServers = false;
                    await owner.Settings.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5)); Pump(window);
                    Assert.False(owner.HasError); Assert.False(owner.Settings.IsDirty);
                    Assert.False(Find<Border>(window, "ClubIntroductionCard").IsEffectivelyVisible);
                    Assert.False(Find<Border>(window, "ClubServersCard").IsEffectivelyVisible);
                }
                finally { window.Close(); }
            }
            var hidden = store.Load(); Assert.False(hidden.ShowClubIntroduction); Assert.False(hidden.ShowClubServers);
            using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(store.FilePath)))
            {
                Assert.False(json.RootElement.GetProperty("showClubIntroduction").GetBoolean());
                Assert.False(json.RootElement.GetProperty("showClubServers").GetBoolean());
            }
            AssertHomeCards(false, false);
            Assert.Equal(hidden, store.Load()); Assert.Equal(0, network.Requests);

            void AssertHomeCards(bool introduction, bool servers)
            {
                using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
                var window = new MainWindow { DataContext = owner }; window.Show(); Pump(window);
                try
                {
                    Assert.False(owner.HasError);
                    Assert.Equal(introduction, owner.Settings.ShowClubIntroduction); Assert.Equal(servers, owner.Settings.ShowClubServers);
                    Assert.Equal(introduction, owner.Home.ShowClubIntroduction); Assert.Equal(servers, owner.Home.ShowClubServers);
                    Assert.Equal(introduction, Find<Border>(window, "ClubIntroductionCard").IsEffectivelyVisible);
                    Assert.Equal(servers, Find<Border>(window, "ClubServersCard").IsEffectivelyVisible);
                }
                finally { window.Close(); }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class RejectPresentationNetwork : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("Presentation preferences must not require network access."); }
    }

    [Fact]
    public async Task IsolatedPresentationStoreIsAtomicAndRejectsInvalidValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wpf-settings-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsPresentationStore(directory);
        try
        {
            var preferences = new SettingsPresentationPreferences(true, true, false, 75);
            await store.SaveAsync(preferences, TestContext.Current.CancellationToken);
            Assert.Equal(preferences, store.Load());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(preferences with { WindowOpacity = 1 }, TestContext.Current.CancellationToken));
            Assert.Equal(preferences, store.Load());
            Assert.Single(Directory.GetFiles(directory));
            Assert.DoesNotContain("password", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
