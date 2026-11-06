using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CommunityShellUiTests
{
    private static void Pump(Window w) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    [AvaloniaFact]
    public void AccountSelectorAndBothActionsAreSquareAndShareTopAndBottom()
    {
        using var root = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        var window = new Window { Width = 300, Height = 560, Content = new AccountsView { DataContext = root.Accounts } }; window.Show(); Pump(window);
        try
        {
            var selector = Find<ComboBox>(window, "AccountSelector");
            foreach (var name in new[] { "BeginAddAccountButton", "RequestAccountRemovalButton" })
            {
                var button = Find<Button>(window, name); Assert.Equal(new Size(32, 32), button.Bounds.Size);
                Assert.Equal(selector.Bounds.Top, button.Bounds.Top); Assert.Equal(selector.Bounds.Bottom, button.Bounds.Bottom);
                Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content);
            }
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CatalogLoadsOnEntryAndPackageFilesDoNotCreateInstallOrLaunchAnInstance()
    {
        var service = new CommunityFixture { InstallBehavior = _ => throw new Xunit.Sdk.XunitException("Standalone files must not install an instance.") }; using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        Assert.Equal(0, service.CatalogCalls); root.ShowDownloadsCommand.Execute(null);
        await root.Downloads.RefreshCatalogCommand.ExecutionTask!;
        Assert.Equal(1, service.CatalogCalls); Assert.NotEmpty(root.Downloads.LatestVersions);
        root.SelectedPage = root.Home; root.ShowDownloadsCommand.Execute(null); Assert.Equal(1, service.CatalogCalls);
        root.Downloads.SelectCategory("installer-minecraft"); Assert.True(root.Downloads.IsCatalogPage); Assert.False(root.Downloads.IsInstallerPage);
        await root.Downloads.OpenPackageVersionAsync(root.Downloads.LatestVersions[0].Version);
        Assert.True(root.Downloads.IsPackageDetail); Assert.False(root.Downloads.IsConfiguration);
        var before = service.Snapshot.Instances.ToArray(); var row = Assert.Single(root.Downloads.MinecraftPackages);
        var path = Path.Combine(FixturePaths.Create("minecraft-packages"), "minecraft.jar");
        await root.Downloads.SaveMinecraftPackageAsync(row, path);
        Assert.Equal((row.Version, "client", path), service.SavedPackage); Assert.Equal(before, service.Snapshot.Instances);
        Assert.Null(service.LastLaunchServerId);
        root.Downloads.BackToListCommand.Execute(null); Assert.True(root.Downloads.IsCatalogPage);
        root.Downloads.SelectCategory("minecraft"); root.Downloads.SelectVersion(root.Downloads.LatestVersions[0].Version);
        Assert.True(root.Downloads.IsConfiguration); Assert.False(root.Downloads.IsPackageDetail);
    }
    [AvaloniaFact]
    public async Task InAppClubHistoryReadsArticleAndEscapeClosesOverlayAtMinimumSize()
    {
        using var root = new MainWindowViewModel(new CommunityFixture(), new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = root }; window.Show(); Pump(window);
        try
        {
            await root.Home.OpenReleasesCommand.ExecuteAsync(null); Pump(window);
            Assert.True(root.Home.IsUpdatesOpen); var row = Assert.Single(root.Home.UpdateItems);
            Assert.Equal("Fixture article body", row.Body); Assert.True(row.IsExpanded);
            var close = Find<Button>(window, "CloseClubUpdatesButton");
            var p = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), window)!.Value;
            Assert.InRange(p.X, 0, 880); Assert.InRange(p.Y, 0, 560);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(window);
            Assert.False(root.Home.IsUpdatesOpen); Assert.DoesNotContain(window.GetVisualDescendants(), c => c is ClubUpdatesOverlay);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CatalogIconsResolveToImagesAndCollapsedVersionGroupsFillPageWidth()
    {
        using var root = new MainWindowViewModel(new CommunityFixture(), new FakeExternalLauncher());
        var vm = root.Downloads; await vm.RefreshCatalogCommand.ExecuteAsync(null);
        var window = new Window { Width = 976, Height = 636, Content = new DownloadsView { DataContext = vm } }; window.Show(); Pump(window);
        try
        {
            var latest = Find<ItemsControl>(window, "LatestGameVersions");
            Assert.NotEmpty(latest.GetVisualDescendants().OfType<Image>());
            Assert.All(latest.GetVisualDescendants().OfType<Image>(), image => Assert.NotNull(image.Source));
            var group = Assert.Single(Find<ItemsControl>(window, "GameVersionGroups").GetVisualDescendants().OfType<Expander>());
            Assert.True(group.Bounds.Width >= latest.Bounds.Width, $"Group {group.Bounds.Width}; latest {latest.Bounds.Width}");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void GatewayOnlineCountsUseMaximumAndDistinguishUnknownFromZero()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        service.Publish(service.Snapshot with { Servers = [new("none", "none", ""), new("smp", "SMP", "smp", OnlinePlayers: 2), new("create", "Create", "create", OnlinePlayers: 2)] });
        Assert.Equal("2", root.Home.OnlinePlayerSummary);
        service.Publish(service.Snapshot with { Servers = service.Snapshot.Servers.Select(s => s.Id == "create" ? s with { OnlinePlayers = null } : s).ToArray() });
        Assert.Equal("2+", root.Home.OnlinePlayerSummary);
        service.Publish(service.Snapshot with { Servers = [new("smp", "SMP", "smp", OnlinePlayers: 0)] }); Assert.Equal("0", root.Home.OnlinePlayerSummary);
        service.Publish(service.Snapshot with { Servers = [new("smp", "SMP", "smp")] }); Assert.Equal("未知", root.Home.OnlinePlayerSummary);
    }
    private sealed class CommunityFixture : FakeLauncherService, IClubContentService, IMinecraftPackageService, ILauncherCatalogService
    {
        private static readonly ClubUpdateNotice Notice = new(new(2026, 1, 2), "Fixture update", new("https://conduit-club.github.io/updates/2026-01-02/"));
        public int CatalogCalls { get; private set; }
        public (string Version, string Kind, string Destination)? SavedPackage { get; private set; }
        public Task<ClubUpdateHistory> GetClubUpdatesAsync(bool refresh = false, CancellationToken ct = default) => Task.FromResult(new ClubUpdateHistory([Notice], DateTimeOffset.UtcNow));
        public Task<ClubUpdateArticle> GetClubUpdateArticleAsync(ClubUpdateNotice notice, bool refresh = false, CancellationToken ct = default) => Task.FromResult(new ClubUpdateArticle(notice, "Fixture article body"));
        public Task<IReadOnlyList<MinecraftPackage>> GetMinecraftPackagesAsync(string version, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MinecraftPackage>>([new("client", "minecraft-client.jar", 42, new string('a', 40))]);
        public Task DownloadMinecraftPackageAsync(string version, string kind, string destination, CancellationToken ct = default) { SavedPackage = (version, kind, destination); return Task.CompletedTask; }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) { CatalogCalls++; return Task.FromResult(new GameCatalog([new("fixture-game", "release", DateTimeOffset.UtcNow, true)], "fixture")); }
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string game, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id, string? game = null, string? loader = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
