using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfDownloadsLoaderTests
{
    private static string Favorites() => Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r5", Guid.NewGuid().ToString("N"), "favorites.json");
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    [AvaloniaFact]
    public async Task GenericLoaderSelectionIsExplicitMutuallyExclusiveAndPreservesBaseVersion()
    {
        var service = new FakeLauncherService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var fixture = new LoaderFixture(service);
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), loaderService: fixture);
        Assert.Empty(fixture.Requests); vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow));
        foreach (var loader in vm.AdditionalLoaders)
        {
            Assert.Null(loader.SelectedVersion); await loader.RefreshCommand.ExecuteAsync(null); Assert.Null(loader.SelectedVersion);
            loader.SelectedVersion = loader.Versions.Single(); Assert.Null(vm.SelectedFabric); Assert.Equal(loader.Id, vm.SelectedAdditionalLoader!.Loader);
            vm.InstallationName = "Explicit " + loader.Title;
            await vm.InstallVersionCommand.ExecuteAsync(null);
            Assert.Equal(("Explicit " + loader.Title, "1.21.1", loader.Id, "fixture-" + loader.Id), fixture.Created.Last());
            var instance = service.Snapshot.Instances.Single(i => i.Name == "Explicit " + loader.Title);
            Assert.Equal("1.21.1", instance.GameVersionId); Assert.StartsWith(loader.Id + "-profile", instance.VersionId); Assert.True(instance.Installed);
            Assert.Equal(0, service.VanillaCreateCalls); Assert.Equal(0, service.FabricCreateCalls);
            vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow));
        }
        await vm.AdditionalLoaders[0].RefreshCommand.ExecuteAsync(null); vm.AdditionalLoaders[0].SelectedVersion = vm.AdditionalLoaders[0].Versions[0];
        vm.SelectedFabric = new("fixture-fabric", true); Assert.Null(vm.SelectedAdditionalLoader);
        vm.AdditionalLoaders[1].SelectedVersion = new("neoforge", "fixture-neoforge", true); Assert.Null(vm.SelectedFabric);
        vm.ClearFabricCommand.Execute(null); Assert.Null(vm.SelectedAdditionalLoader); Assert.Contains("原版", vm.InstallationSummary);
    }

    [AvaloniaFact]
    public async Task UnsupportedChoiceAndUnverifiedGameStayDisabledInActualControls()
    {
        var service = new FakeLauncherService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var fixture = new LoaderFixture(service) { Unsupported = true };
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), loaderService: fixture);
        var window = new Window { Width = 976, Height = 636, Content = new DownloadsView { DataContext = vm } }; window.Show(); Pump(window);
        try
        {
            vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow)); var forge = vm.AdditionalLoaders[0];
            await forge.RefreshCommand.ExecuteAsync(null); Pump(window);
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Forge")).IsExpanded = true; Pump(window);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "AdditionalLoaderVersions" && ReferenceEquals(c.DataContext, forge));
            Assert.False(list.GetVisualDescendants().OfType<ListBoxItem>().Single().IsEffectivelyEnabled);
            forge.SelectedVersion = forge.Versions[0]; Assert.False(vm.InstallVersionCommand.CanExecute(null)); Assert.Contains("fixture unsupported", vm.InstallationValidation);
            fixture.Unsupported = false; vm.SelectVersion(new("1.20.4", "release", DateTimeOffset.UtcNow)); await forge.RefreshCommand.ExecuteAsync(null); forge.SelectedVersion = forge.Versions[0];
            Assert.False(vm.InstallVersionCommand.CanExecute(null)); Assert.Contains("1.21.1", vm.InstallationValidation); Assert.Empty(fixture.Created);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateCatalogAndCancelledReadCannotSelectOrCreateAnotherRoute()
    {
        var service = new FakeLauncherService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var fixture = new LoaderFixture(service);
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), loaderService: fixture); vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow));
        var response = new TaskCompletionSource<IReadOnlyList<LoaderVersionChoice>>(); fixture.Behavior = (_, _, _) => response.Task;
        var pending = vm.AdditionalLoaders[0].RefreshCommand.ExecuteAsync(null); vm.SelectCategory("mod");
        response.SetResult([new("forge", "stale-fixture", true)]); await pending;
        Assert.Empty(vm.AdditionalLoaders[0].Versions); Assert.Null(vm.SelectedAdditionalLoader); Assert.Empty(fixture.Created);
        vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow));
        fixture.Behavior = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; };
        pending = vm.AdditionalLoaders[0].RefreshCommand.ExecuteAsync(null); owner.CancelOperationCommand.Execute(null); await pending;
        Assert.Contains("取消", vm.AdditionalLoaders[0].Status); Assert.Empty(vm.AdditionalLoaders[0].Versions);
    }

    [AvaloniaFact]
    public async Task RemovedCatalogChoiceRemainsExplicitlyInvalidUntilUserChoosesVanilla()
    {
        var service = new FakeLauncherService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var fixture = new LoaderFixture(service);
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), loaderService: fixture);
        vm.SelectVersion(new("1.21.1", "release", DateTimeOffset.UtcNow)); var forge = vm.AdditionalLoaders[0];
        await forge.RefreshCommand.ExecuteAsync(null); forge.SelectedVersion = forge.Versions[0];
        fixture.Behavior = (_, _, _) => Task.FromResult<IReadOnlyList<LoaderVersionChoice>>([]); await forge.RefreshCommand.ExecuteAsync(null);
        Assert.NotNull(vm.SelectedAdditionalLoader); Assert.False(vm.SelectedAdditionalLoader.CanInstall); Assert.False(vm.InstallVersionCommand.CanExecute(null));
        Assert.Contains("此前选择", vm.InstallationValidation); Assert.Contains("Forge", vm.InstallationSummary); Assert.Empty(fixture.Created);
        forge.ClearCommand.Execute(null); Assert.Null(vm.SelectedAdditionalLoader); Assert.Contains("原版", vm.InstallationSummary); Assert.True(vm.InstallVersionCommand.CanExecute(null));
    }

    private sealed class LoaderFixture(FakeLauncherService service) : ILauncherCatalogService, ILoaderInstanceService
    {
        public bool Unsupported;
        public List<(string Name, string Game, string Loader, string Version)> Created { get; } = [];
        public List<(string Loader, string Game)> Requests { get; } = [];
        public Func<string, string, CancellationToken, Task<IReadOnlyList<LoaderVersionChoice>>>? Behavior;
        public Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken cancellationToken = default)
        { Requests.Add((loader, gameVersion)); return Behavior?.Invoke(loader, gameVersion, cancellationToken) ?? Task.FromResult<IReadOnlyList<LoaderVersionChoice>>([new(loader, "fixture-" + loader, true, Unsupported ? "fixture unsupported installer" : null, !Unsupported)]); }
        public Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion, CancellationToken cancellationToken = default)
        {
            Created.Add((name, gameVersion, loader, loaderVersion));
            var instance = new GameInstance(Guid.NewGuid().ToString("N"), name, loader + "-profile-" + loaderVersion, "/fixture/" + loader, Loader: loader, LoaderVersion: loaderVersion, GameVersionId: gameVersion);
            service.Publish(service.Snapshot with { Instances = service.Snapshot.Instances.Append(instance).ToArray(), SelectedInstanceId = instance.Id }); return Task.FromResult(instance);
        }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
