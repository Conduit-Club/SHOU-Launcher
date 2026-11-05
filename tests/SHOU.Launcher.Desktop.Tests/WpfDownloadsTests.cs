using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfDownloadsTests
{
    private static readonly DateTimeOffset FixtureDate = new(2025, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private static string FixturePath(string name) => Path.Combine(FixturePaths.Create("downloads"), name);
    private static ContentProject Project(string id = "fixture-project", string? icon = null) => new(id, id, "Fixture Resource", "Fixture description", "Fixture author", 12, "mod", icon, new("https://modrinth.com/mod/" + id));
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task CatalogIsExplicitAndGroupsReturnedVersionsWithoutDiscardingUnsupportedIds()
    {
        var catalog = new CatalogFixture { Games = new([
            new("fixture-release", "release", FixtureDate, true), new("fixture-preview", "snapshot", FixtureDate, true),
            new("fixture-alpha", "old_alpha", FixtureDate), new("Historic Name With Space", "snapshot", FixtureDate, CanInstall: false)
        ], "fixture-source", Partial: true) };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        Assert.Equal(0, catalog.GameCalls);
        await vm.RefreshCatalogCommand.ExecuteAsync(null);
        Assert.Equal(1, catalog.GameCalls);
        Assert.Equal(2, vm.LatestVersions.Count);
        Assert.Equal(4, vm.VersionGroups.Sum(g => g.Versions.Count));
        Assert.Contains("精简目录", vm.CatalogSource);
        var unsupported = vm.VersionGroups.SelectMany(g => g.Versions).Single(v => !v.Version.CanInstall);
        Assert.False(unsupported.SelectCommand.CanExecute(null));
        vm.SelectVersion(unsupported.Version);
        Assert.False(vm.IsConfiguration);
        vm.LatestVersions[0].SelectCommand.Execute(null);
        Assert.True(vm.IsConfiguration);
    }

    [AvaloniaFact]
    public async Task CatalogConfigurationCreatesAndInstallsVanillaAndExplicitFabric()
    {
        var fake = new FakeLauncherService();
        var catalog = new CatalogFixture { Fabric = [new("0.99-fixture", true)] };
        using var owner = new MainWindowViewModel(fake, new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        var version = new GameCatalogVersion("fixture-game", "release", FixtureDate);
        vm.SelectVersion(version);
        vm.InstallationName = fake.Snapshot.Instances[0].Name;
        Assert.False(vm.InstallVersionCommand.CanExecute(null));
        Assert.Contains("同名", vm.InstallationValidation);
        vm.InstallationName = "Directory fixture vanilla";
        await vm.InstallVersionCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.VanillaCreateCalls);
        Assert.True(fake.Snapshot.Instances.Single(i => i.Name == "Directory fixture vanilla").Installed);
        Assert.True(vm.HasCreatedInstance);
        vm.SelectVersion(version);
        await vm.RefreshFabricCommand.ExecuteAsync(null);
        Assert.Equal("fixture-game", catalog.LastFabricGame);
        Assert.Null(vm.SelectedFabric);
        vm.SelectedFabric = vm.FabricVersions.Single();
        await vm.InstallVersionCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.FabricCreateCalls);
        Assert.Equal(("fixture-game-Fabric_0.99-fixture", "fixture-game", "0.99-fixture"), fake.LastFabricRequest);
        Assert.Contains("已创建并安装", vm.InstallStatus);
    }

    [AvaloniaFact]
    public async Task CancelAndFailureAreVisibleRetryableAndDoNotCreateCatalogSuccess()
    {
        var catalog = new CatalogFixture { GameBehavior = async ct => { await Task.Delay(Timeout.Infinite, ct); return new([], "unreachable"); } };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        var pending = vm.RefreshCatalogCommand.ExecuteAsync(null);
        Assert.True(owner.IsBusy);
        owner.CancelOperationCommand.Execute(null);
        await pending;
        Assert.False(vm.HasCatalog);
        Assert.Contains("取消", vm.RequestStatus);
        catalog.GameBehavior = _ => throw new IOException("catalog failure api_key=fixture-private-key");
        await vm.RefreshCatalogCommand.ExecuteAsync(null);
        Assert.Contains("catalog failure", vm.RequestStatus);
        Assert.DoesNotContain("fixture-private-key", vm.RequestStatus);
        Assert.False(vm.HasCatalog);
        Assert.True(vm.RefreshCatalogCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SearchFiltersPaginationAndStaleNavigationFollowTheRequestedRoute()
    {
        var catalog = new CatalogFixture { SearchBehavior = (query, _) => Task.FromResult<IReadOnlyList<ContentProject>>(Enumerable.Range(0, 30).Select(i => Project("fixture-" + i)).ToArray()) };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        vm.SelectCategory("mod");
        vm.QueryText = "  query  "; vm.GameVersionFilter = "fixture-game"; vm.LoaderFilter = "Fabric"; vm.SelectedSort = vm.SortChoices.Single(s => s.Id == "downloads");
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(new ContentQuery("mod", "query", "fixture-game", "fabric", "downloads", 0), catalog.Queries[0]);
        Assert.True(vm.NextPageCommand.CanExecute(null));
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(30, catalog.Queries[1].Offset);
        Assert.True(vm.PreviousPageCommand.CanExecute(null));
        var response = new TaskCompletionSource<IReadOnlyList<ContentProject>>();
        catalog.SearchBehavior = (_, _) => response.Task;
        var pending = vm.SearchCommand.ExecuteAsync(null);
        vm.SelectCategory("resourcepack");
        response.SetResult([Project("stale-project")]); await pending;
        Assert.Empty(vm.Projects);
        Assert.Equal("resourcepack", vm.SelectedCategory.Id);
        vm.SelectCategory("world");
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.Contains("尚未提供", vm.SearchCapabilityReason);
    }

    [AvaloniaFact]
    public async Task ResourceDetailsDownloadTheReturnedFileAndFavoritesRoundTripWithoutFakeSuccess()
    {
        var file = new ContentFile("fixture-content.jar", new("https://cdn.modrinth.com/data/fixture.jar"), new string('a', 40), 1234, true);
        var catalog = new CatalogFixture { Releases = [new("fixture-release", "Fixture release", "fixture-build", FixtureDate, ["fixture-game"], ["fabric"], [file])], DownloadPath = FixturePath("fixture-content.jar") };
        var path = FixturePath("favorites.json");
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using (var vm = new DownloadsPageViewModel(owner, catalog, path))
        {
            vm.SelectCategory("mod");
            await vm.OpenProjectAsync(Project());
            Assert.True(vm.IsResourceDetail);
            Assert.Equal("fixture-project", catalog.LastProject);
            await vm.ReleaseGroups.Single().Releases.Single().Files.Single().DownloadCommand.ExecuteAsync(null);
            Assert.Same(file, catalog.LastDownloadedFile);
            Assert.Equal(catalog.DownloadPath, vm.LastDownloadPath);
            Assert.Contains("尚未自动装入", vm.RequestStatus);
            await vm.ToggleFavoriteCommand.ExecuteAsync(null);
            Assert.True(vm.IsProjectFavorite);
            Assert.True(File.Exists(path));
        }
        using (var reopened = new DownloadsPageViewModel(owner, catalog, path))
        {
            Assert.Single(reopened.FavoriteProjects);
            Assert.Equal("fixture-project", reopened.FavoriteProjects[0].Project.Id);
            await reopened.OpenProjectAsync(Project());
            Assert.Equal("取消收藏", reopened.FavoriteAction);
            await reopened.ToggleFavoriteCommand.ExecuteAsync(null);
            Assert.Empty(reopened.FavoriteProjects);
        }
        var malformed = FixturePath("unreadable-favorites.json");
        Directory.CreateDirectory(Path.GetDirectoryName(malformed)!); await File.WriteAllTextAsync(malformed, "{not valid JSON");
        using var blocked = new DownloadsPageViewModel(owner, catalog, malformed);
        await blocked.OpenProjectAsync(Project());
        Assert.False(blocked.ToggleFavoriteCommand.CanExecute(null));
        Assert.Equal("{not valid JSON", await File.ReadAllTextAsync(malformed));
        Assert.Contains("原文件已保留", blocked.FavoriteStatus);
    }

    [AvaloniaFact]
    public async Task ThumbnailFailuresKeepResultsAndWindowBindingsRemainValid()
    {
        var catalog = new CatalogFixture { SearchBehavior = (_, _) => Task.FromResult<IReadOnlyList<ContentProject>>([Project(icon: "https://cdn.modrinth.com/fixture.png")]), IconBehavior = (_, _) => throw new IOException("thumbnail fixture failure") };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        var previous = Logger.Sink; var sink = new BindingFailures(); Logger.Sink = sink;
        var window = new Window { Width = 976, Height = 588, Content = new DownloadsView { DataContext = vm } };
        window.Show(); Pump(window);
        try
        {
            Assert.Equal(180, Find<Border>(window, "DownloadsCategoryPanel").Bounds.Width);
            Assert.Equal(0, catalog.GameCalls);
            vm.SelectCategory("mod"); await vm.SearchCommand.ExecuteAsync(null); Pump(window);
            Assert.Single(vm.Projects);
            Assert.Null(vm.Projects[0].Icon);
            Assert.Null(owner.ErrorMessage);
            await vm.Projects[0].OpenCommand.ExecuteAsync(null); Pump(window);
            vm.BackToListCommand.Execute(null); vm.SelectCategory("installer-forge"); Pump(window);
            Assert.True(vm.IsInstallerPage);
            Assert.Contains("尚未迁移", vm.InstallerDescription);
            vm.SelectCategory("tasks"); Pump(window);
            Assert.Contains(window.GetVisualDescendants(), c => c.Name == "DownloadTasks");
            Assert.Empty(sink.Errors);
        }
        finally { window.Close(); Logger.Sink = previous; }
    }

    [AvaloniaFact]
    public async Task SelectingVersionFromAScrolledLongCatalogShowsConfigurationNameAtTop()
    {
        var versions = Enumerable.Range(0, 80).Select(i => new GameCatalogVersion("scroll-fixture-" + i, "release", FixtureDate)).ToArray();
        var catalog = new CatalogFixture { Games = new(versions, "fixture-source") };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        var window = new Window { Width = 976, Height = 588, Content = new DownloadsView { DataContext = vm } };
        window.Show(); Pump(window);
        try
        {
            await vm.RefreshCatalogCommand.ExecuteAsync(null); Pump(window);
            Find<ItemsControl>(window, "GameVersionGroups").GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            Pump(window);
            var scroller = Find<ScrollViewer>(window, "DownloadsPageScroller");
            scroller.Offset = new Vector(0, 1800); Pump(window);
            Assert.True(scroller.Offset.Y > 100);
            vm.SelectVersion(versions[40]); Pump(window);
            Assert.Equal(0, scroller.Offset.Y);
            var name = Find<TextBox>(window, "CatalogInstallationName");
            Assert.True(name.IsEffectivelyVisible);
            var position = name.TranslatePoint(new Point(0, 0), scroller);
            Assert.NotNull(position);
            Assert.InRange(position.Value.Y, 0, 100);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ResourceDetailsUseContextTitleAndBackReturnsToTheSameSearch()
    {
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher(), new CatalogFixture());
        owner.ShowDownloadsCommand.Execute(null);
        owner.Downloads.SelectCategory("mod");
        await owner.Downloads.OpenProjectAsync(Project());
        Assert.True(owner.IsContextPage);
        Assert.Equal("资源下载 - Fixture Resource", owner.NavigationTitle);
        Assert.Equal("download", owner.CurrentSection);
        owner.BackCommand.Execute(null);
        Assert.Same(owner.Downloads, owner.SelectedPage);
        Assert.Equal("mod", owner.Downloads.SelectedCategory.Id);
        Assert.False(owner.IsContextPage);
    }

    [AvaloniaFact]
    public async Task ThumbnailConcurrencyIsBoundedAndDisposalCancelsOutstandingRequests()
    {
        var catalog = new CatalogFixture { SearchBehavior = (_, _) => Task.FromResult<IReadOnlyList<ContentProject>>(Enumerable.Range(0, 12).Select(i => Project("icon-" + i, "https://cdn.modrinth.com/fixture.png")).ToArray()) };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, catalog, FixturePath("favorites.json"));
        var four = new TaskCompletionSource();
        catalog.IconBehavior = async (_, ct) =>
        {
            if (catalog.ActiveIcons == 4) four.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); return [];
        };
        vm.SelectCategory("mod"); var pending = vm.SearchCommand.ExecuteAsync(null);
        await four.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, catalog.MaximumIcons);
        vm.Dispose(); await pending;
        Assert.Equal(0, catalog.ActiveIcons);
        Assert.Empty(vm.Projects);
        Assert.Null(owner.ErrorMessage);
    }

    private sealed class CatalogFixture : ILauncherCatalogService
    {
        public GameCatalog Games { get; set; } = new([], "fixture-source");
        public IReadOnlyList<LoaderCatalogVersion> Fabric { get; set; } = [];
        public IReadOnlyList<ContentRelease> Releases { get; set; } = [];
        public Func<CancellationToken, Task<GameCatalog>>? GameBehavior { get; set; }
        public Func<ContentQuery, CancellationToken, Task<IReadOnlyList<ContentProject>>>? SearchBehavior { get; set; }
        public Func<string, CancellationToken, Task<byte[]>>? IconBehavior { get; set; }
        public int GameCalls { get; private set; }
        public string? LastFabricGame { get; private set; }
        public string? LastProject { get; private set; }
        public List<ContentQuery> Queries { get; } = [];
        public ContentFile? LastDownloadedFile { get; private set; }
        public string DownloadPath { get; set; } = "/fixture/not-a-real-download";
        public int ActiveIcons { get; private set; }
        public int MaximumIcons { get; private set; }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) { GameCalls++; return GameBehavior?.Invoke(cancellationToken) ?? Task.FromResult(Games); }
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) { LastFabricGame = gameVersion; return Task.FromResult(Fabric); }
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) { Queries.Add(query); return SearchBehavior?.Invoke(query, cancellationToken) ?? Task.FromResult<IReadOnlyList<ContentProject>>([]); }
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) { LastProject = projectId; return Task.FromResult(Releases); }
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) { LastDownloadedFile = file; return Task.FromResult(DownloadPath); }
        public async Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default)
        {
            ActiveIcons++; MaximumIcons = Math.Max(MaximumIcons, ActiveIcons);
            try { return IconBehavior is null ? [] : await IconBehavior(iconUrl, cancellationToken); }
            finally { ActiveIcons--; }
        }
    }
    private sealed class BindingFailures : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) { if (IsEnabled(level, area)) Errors.Add(messageTemplate); }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) { if (IsEnabled(level, area)) Errors.Add(messageTemplate + " " + string.Join(" | ", propertyValues)); }
    }
}
