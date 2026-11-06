using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CatalogNavigationTests
{
    [AvaloniaFact]
    public async Task RoutineCatalogSuccessIsQuietButOfflineNoticeAndErrorsRemainVisible()
    {
        var service = new CatalogService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        owner.ShowDownloadsCommand.Execute(null); await Settle(() => !owner.IsBusy);
        Assert.False(owner.Downloads.ShowRequestStatus);
        service.Next = _ => Task.FromResult(CatalogService.Games with { Notice = "Offline cache fixture notice" });
        await owner.Downloads.RefreshCatalogCommand.ExecuteAsync(null); Assert.True(owner.Downloads.ShowRequestStatus);
        owner.Downloads.SelectCategory("mod"); Assert.False(owner.Downloads.ShowRequestStatus);
        owner.Downloads.SelectCategory("minecraft"); Assert.True(owner.Downloads.ShowRequestStatus);
        service.Next = _ => throw new IOException("Fixture network error");
        await owner.Downloads.RefreshCatalogCommand.ExecuteAsync(null); Assert.True(owner.Downloads.ShowRequestStatus); Assert.True(owner.HasError);
        service.Next = _ => Task.FromResult(CatalogService.Games);
        await owner.Downloads.RefreshCatalogCommand.ExecuteAsync(null); Assert.False(owner.Downloads.ShowRequestStatus);
    }
    [AvaloniaFact]
    public async Task FirstEntryUsesCacheAndManualRefreshForcesNetworkWithoutLosingRowsOnNavigation()
    {
        var service = new CatalogService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        owner.ShowDownloadsCommand.Execute(null); await Settle(() => !owner.IsBusy);
        Assert.Equal(1, service.Reads); Assert.Equal(0, service.Refreshes); Assert.Contains("本地缓存", owner.Downloads.CatalogSource);
        var pending = new TaskCompletionSource<GameCatalog>(); service.Next = _ => pending.Task;
        var refresh = owner.Downloads.RefreshCatalogCommand.ExecuteAsync(null);
        owner.Downloads.SelectCategory("mod"); pending.SetResult(CatalogService.Games with { FromCache = false }); await refresh;
        Assert.Equal(1, service.Refreshes); Assert.Contains("输入条件", owner.Downloads.RequestStatus);
        owner.Downloads.SelectCategory("installer-minecraft");
        Assert.True(owner.Downloads.HasCatalog); Assert.Single(owner.Downloads.LatestVersions);
        Assert.Equal(1, service.Reads); Assert.Equal(1, service.Refreshes);
    }
    [AvaloniaFact]
    public async Task InitialCatalogRequestQueuedDuringAnotherOperationRunsAfterwards()
    {
        var service = new CatalogService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        var gate = new TaskCompletionSource(); var operation = owner.RunAsync("fixture unrelated operation", _ => gate.Task);
        owner.ShowDownloadsCommand.Execute(null); Assert.Equal(0, service.Reads);
        gate.SetResult(); await operation; await Settle(() => service.Reads == 1 && !owner.IsBusy);
        Assert.True(owner.Downloads.HasCatalog); Assert.Equal(1, service.Reads);
    }
    [AvaloniaFact]
    public async Task ChangingDownloadSourcesDiscardsOldInFlightCatalogAndReadsTheNewSource()
    {
        var response = new TaskCompletionSource<GameCatalog>();
        var service = new CatalogService { Next = _ => response.Task }; using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        owner.ShowDownloadsCommand.Execute(null); Assert.Equal(1, service.Reads);
        await service.SaveSettingsAsync(service.Snapshot.Settings with { DownloadSourcesJson = "[]" });
        service.Next = _ => Task.FromResult(CatalogService.Games with { Source = "new-source.example" });
        response.SetResult(CatalogService.Games with { Source = "obsolete.example" });
        await Settle(() => service.Reads == 2 && !owner.IsBusy);
        Assert.Contains("new-source.example", owner.Downloads.CatalogSource); Assert.DoesNotContain("obsolete", owner.Downloads.CatalogSource);
    }
    [AvaloniaFact]
    public async Task PackageBrowsingPreservesAutomaticInstallationNameVersionAndLoaderDraft()
    {
        var service = new CatalogService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        owner.ShowDownloadsCommand.Execute(null); await Settle(() => !owner.IsBusy);
        var vm = owner.Downloads; vm.SelectVersion(CatalogService.Games.Versions[0]); vm.InstallationName = "保留 中文 draft";
        await vm.RefreshFabricCommand.ExecuteAsync(null); vm.SelectedFabric = vm.FabricVersions.Single();
        vm.BackToListCommand.Execute(null); vm.SelectCategory("installer-minecraft");
        await vm.OpenPackageVersionAsync(new("other-version", "release", DateTimeOffset.UtcNow));
        Assert.Equal("other-version", vm.PackageVersionId); Assert.Equal("1.21.1", vm.SelectedVersionId);
        vm.BackToListCommand.Execute(null); vm.SelectCategory("minecraft"); vm.SelectVersion(CatalogService.Games.Versions[0]);
        Assert.True(vm.IsConfiguration); Assert.Equal("保留 中文 draft", vm.InstallationName); Assert.Equal("0.16.0", vm.SelectedFabric!.Version);
        Assert.Equal(0, service.VanillaCreateCalls); Assert.Equal(0, service.FabricCreateCalls);
    }
    private static async Task Settle(Func<bool> ready)
    { for (var i = 0; i < 100 && !ready(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.True(ready()); }
    private sealed class CatalogService : FakeLauncherService, ILauncherCatalogService, IMinecraftCatalogCacheService, IMinecraftPackageService
    {
        public static GameCatalog Games => new([new("1.21.1", "release", DateTimeOffset.UnixEpoch, true)], "fixture.example", FetchedAt: DateTimeOffset.UtcNow, FromCache: true);
        public int Reads, Refreshes;
        public Func<CancellationToken, Task<GameCatalog>>? Next;
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) { Reads++; return Next?.Invoke(cancellationToken) ?? Task.FromResult(Games); }
        public Task<GameCatalog> RefreshGameCatalogAsync(CancellationToken cancellationToken = default) { Refreshes++; return Next?.Invoke(cancellationToken) ?? Task.FromResult(Games); }
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([new("0.16.0", true)]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MinecraftPackage>> GetMinecraftPackagesAsync(string versionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MinecraftPackage>>([]);
        public Task DownloadMinecraftPackageAsync(string versionId, string kind, string destinationPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
