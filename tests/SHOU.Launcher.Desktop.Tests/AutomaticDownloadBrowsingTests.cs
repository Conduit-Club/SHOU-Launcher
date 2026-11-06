using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class AutomaticDownloadBrowsingTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.True(condition()); }
    [AvaloniaFact]
    public async Task EverySupportedCommunityTabAutomaticallyShowsDownloadsSortedResourcesWithoutLockingTheShell()
    {
        var source = new BrowseFixture(); using var owner = new MainWindowViewModel(source, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            owner.ShowDownloadsCommand.Execute(null);
            foreach (var kind in new[] { "mod", "modpack", "datapack", "resourcepack", "shader" })
            {
                owner.Downloads.SelectCategory(kind); await Until(() => owner.Downloads.Projects.Any(p => p.Project.Kind == kind)); Pump(window);
                Assert.False(owner.IsBusy); Assert.False(Find<Border>(window, "OperationBar").IsEffectivelyVisible);
                var query = source.Queries.Last(); Assert.Equal(kind, query.Kind); Assert.Empty(query.Search); Assert.Equal("downloads", query.Sort);
                Assert.False(owner.Downloads.ShowRequestStatus); Assert.True(owner.Downloads.HasContentCategoryChoices);
            }
            Assert.Equal(0, source.VanillaCreateCalls); Assert.Equal(0, source.FabricCreateCalls); Assert.Null(source.LastLaunchServerId);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task LoadingStaysInsideRightContentAndLateSearchCannotReplaceANewerQuery()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ContentProject>>();
        var source = new BrowseFixture { Search = (query, _) => query.Search == "" ? pending.Task : Task.FromResult<IReadOnlyList<ContentProject>>([BrowseFixture.Project("new-result", query.Kind)]) };
        using var owner = new MainWindowViewModel(source, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            owner.ShowDownloadsCommand.Execute(null); owner.Downloads.SelectCategory("mod"); Pump(window);
            Assert.True(owner.Downloads.ShowFullPageLoading); Assert.False(owner.IsBusy);
            var loading = Find<Grid>(window, "DownloadLoadingPage"); var sidebar = Find<Border>(window, "DownloadsCategoryPanel");
            Assert.True(loading.Bounds.Left >= sidebar.Bounds.Right); Assert.True(Find<Button>(window, "ToolsNavigationButton").IsEffectivelyEnabled);
            owner.Downloads.QueryText = "new"; await owner.Downloads.SearchCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal("new-result", Assert.Single(owner.Downloads.Projects).Project.Id);
            pending.SetResult([BrowseFixture.Project("obsolete", "mod")]);
            await Until(() => !owner.Downloads.IsLoading);
            Assert.Equal("new-result", Assert.Single(owner.Downloads.Projects).Project.Id);
            Assert.False(owner.Downloads.ShowFullPageLoading); Assert.False(owner.HasError);
        }
        finally { pending.TrySetResult([]); window.Close(); }
    }
    [AvaloniaFact]
    public void SelectingGameAutomaticallyReadsAllLoaderHeadersWithoutSelectingAnyOrCreatingAnInstance()
    {
        var source = new BrowseFixture(); using var owner = new MainWindowViewModel(source, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            owner.ShowDownloadsCommand.Execute(null); owner.Downloads.SelectVersion(new("1.21.1", "release", DateTimeOffset.UnixEpoch)); Pump(window);
            Assert.Equal(4, source.LoaderReads.Count); Assert.Null(owner.Downloads.SelectedFabric); Assert.Null(owner.Downloads.SelectedAdditionalLoader);
            var configuration = Find<StackPanel>(window, "MinecraftInstallConfiguration");
            var rows = configuration.GetVisualDescendants().OfType<Expander>().ToArray(); Assert.Equal(4, rows.Length);
            foreach (var row in rows) { Assert.False(row.IsExpanded); Assert.InRange(row.Bounds.Width, configuration.Bounds.Width - 1, configuration.Bounds.Width + 1); Assert.Contains(row.GetVisualDescendants().OfType<Image>(), image => image.Source is not null); }
            Assert.False(owner.IsBusy); Assert.Equal(0, source.VanillaCreateCalls); Assert.Equal(0, source.FabricCreateCalls);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task InstallerDirectoryLoadsOnEntryAndLongOperationsDoNotCreateABottomProgressRow()
    {
        var source = new BrowseFixture(); using var owner = new MainWindowViewModel(source, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        var gate = new TaskCompletionSource();
        try
        {
            owner.ShowDownloadsCommand.Execute(null); owner.Downloads.SelectCategory("installer-forge"); await Until(() => owner.Downloads.HasInstallerVersions); Pump(window);
            Assert.Single(owner.Downloads.InstallerVersionGroups); Assert.True(owner.Downloads.InstallerVersionGroups[0].IsExpanded);
            var page = Find<ContentControl>(window, "PageContent"); var size = page.Bounds.Size;
            var operation = owner.RunAsync("controlled shell operation", _ => gate.Task); Pump(window);
            Assert.Equal(size, page.Bounds.Size);
            var overlay = Find<Border>(window, "OperationBar"); Assert.True(overlay.IsEffectivelyVisible); Assert.Equal(page.Bounds.Height, overlay.Bounds.Height);
            Assert.Empty(overlay.GetVisualDescendants().OfType<ProgressBar>());
            gate.SetResult(); await operation;
            Assert.Equal(0, source.VanillaCreateCalls); Assert.Equal(0, source.FabricCreateCalls);
        }
        finally { gate.TrySetResult(); window.Close(); }
    }
    private sealed class BrowseFixture : FakeLauncherService, ILauncherCatalogService, ILoaderInstanceService, IInstallerCatalogService, IContentCategoryCatalogService
    {
        public List<ContentQuery> Queries { get; } = [];
        public List<string> LoaderReads { get; } = [];
        public Func<ContentQuery, CancellationToken, Task<IReadOnlyList<ContentProject>>>? Search;
        public static ContentProject Project(string id, string kind) => new(id, id, "资源 " + id, "Controlled public fixture", "Fixture", 283201701, kind, null, new("https://modrinth.com/mod/" + id), Categories: ["library", "fabric"], GameVersions: ["1.21.1"], Updated: DateTimeOffset.UnixEpoch);
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameCatalog([new("1.21.1", "release", DateTimeOffset.UnixEpoch, true)], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default)
        { LoaderReads.Add("fabric"); return Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([new("0.16.0", true)]); }
        public Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken cancellationToken = default)
        { LoaderReads.Add(loader); return Task.FromResult<IReadOnlyList<LoaderVersionChoice>>([new(loader, "fixture-1", true)]); }
        public Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default)
        { Queries.Add(query); return Search?.Invoke(query, cancellationToken) ?? Task.FromResult<IReadOnlyList<ContentProject>>([Project(query.Kind, query.Kind)]); }
        public Task<IReadOnlyList<ContentCategory>> GetContentCategoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentCategory>>([new("library", "mod"), new("adventure", "modpack"), new("16x", "resourcepack"), new("realistic", "shader")]);
        public Task<InstallerCatalog> GetInstallerCatalogAsync(string loader, CancellationToken cancellationToken = default) => Task.FromResult(new InstallerCatalog([new(loader, "26.3", "66.1.5", "26.3-66.1.5")]));
        public Task SaveInstallerAsync(InstallerVersion version, string destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
