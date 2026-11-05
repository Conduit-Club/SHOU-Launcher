using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfDownloadsInstallationTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Visible<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);
    private static string Favorites() => Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data", "r5", Guid.NewGuid().ToString("N"), "favorites.json");
    [AvaloniaFact]
    public async Task ResourceButtonsInstallOnlyIntoExplicitCompatibleInstanceAndCacheRemainsSeparate()
    {
        var service = new FakeLauncherService();
        service.Publish(service.Snapshot with { Instances = [new("vanilla", "Vanilla target", "1.21.1", "/fixture/vanilla"), new("fabric", "Fabric target", "fabric-loader-0.16.5-1.21.1", "/fixture/fabric", Loader: "fabric", LoaderVersion: "0.16.5")], SelectedInstanceId = "fabric" });
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var fixture = new ContentFixture();
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), fixture, fixture); vm.Refresh(service.Snapshot);
        var window = new Window { Width = 976, Height = 636, Content = new DownloadsView { DataContext = vm } }; window.Show(); Pump(window);
        try
        {
            vm.SelectCategory("mod"); await vm.SearchCommand.ExecuteAsync(null); await vm.Projects.Single().OpenCommand.ExecuteAsync(null); Pump(window);
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, vm.ReleaseGroups.Single().Header)).IsExpanded = true; Pump(window);
            var row = vm.ReleaseGroups.Single().Releases.Single().Files.Single();
            Assert.True(row.InstallCommand.CanExecute(null)); Assert.Equal("fabric", vm.SelectedInstallTarget!.Instance.Id);
            var selector = Visible<ComboBox>(window, "ContentInstallTargetSelector");
            selector.SelectedItem = vm.ContentInstallTargets.Single(t => t.Instance.Id == "vanilla"); Pump(window);
            Assert.False(row.InstallCommand.CanExecute(null));
            Assert.False(Visible<Button>(window, "InstallResourceFileButton").IsEffectivelyEnabled);
            selector.SelectedItem = vm.ContentInstallTargets.Single(t => t.Instance.Id == "fabric"); Pump(window);
            Assert.True(Visible<Button>(window, "InstallResourceFileButton").IsEffectivelyEnabled);
            await row.InstallCommand.ExecuteAsync(null); Assert.Equal("fabric", fixture.Installed!.InstanceId); Assert.Equal("release", fixture.Installed.ReleaseId);
            Assert.Contains("已装入 Fabric target", vm.ResourceInstallStatus);
            await row.DownloadCommand.ExecuteAsync(null); Assert.Equal(1, fixture.InstallCalls); Assert.Equal(1, fixture.CacheCalls);
            Pump(window);
            Assert.Contains("缓存", vm.RequestStatus); Assert.Equal("下载到缓存", Visible<Button>(window, "DownloadResourceFileButton").Content);
            fixture.ServerOnly = true; await vm.RefreshReleasesCommand.ExecuteAsync(null);
            var serverRelease = vm.ReleaseGroups.Single().Releases.Single(); Assert.False(serverRelease.Files.Single().InstallCommand.CanExecute(null)); Assert.Contains("服务器专用", serverRelease.Detail);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LocalPackOptionalSelectionAndUnsupportedLoaderBindToActualDisabledImportButton()
    {
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher()); var fixture = new ContentFixture();
        using var vm = new DownloadsPageViewModel(owner, fixture, Favorites(), fixture, fixture);
        var window = new Window { Width = 976, Height = 636, Content = new DownloadsView { DataContext = vm } }; window.Show(); Pump(window);
        try
        {
            vm.SelectCategory("modpack"); await vm.ReadLocalPackAsync("/fixture/local.mrpack"); Pump(window);
            Assert.True(vm.ShowPackImport); Assert.Single(vm.OptionalPackFiles); Assert.False(vm.OptionalPackFiles[0].Selected);
            vm.OptionalPackFiles[0].Selected = true; vm.PackInstanceName = "Explicit local pack";
            Assert.True(vm.ImportLocalPackCommand.CanExecute(null)); await vm.ImportLocalPackCommand.ExecuteAsync(null);
            Assert.Equal("Explicit local pack", fixture.Imported!.InstanceName); Assert.Equal(new[] { "mods/optional.jar" }, fixture.Imported.OptionalClientFiles);
            Assert.Contains("首次启动", vm.PackStatus);
            fixture.Unsupported = true; await vm.ReadLocalPackAsync("/fixture/forge.mrpack"); Pump(window);
            Assert.False(vm.ImportLocalPackCommand.CanExecute(null)); Assert.False(Visible<Button>(window, "ImportModrinthPackButton").IsEffectivelyEnabled); Assert.Contains("Forge", vm.PackStatus);
        }
        finally { window.Close(); }
    }

    private sealed class ContentFixture : ILauncherCatalogService, IContentInstallationService, IModrinthPackService
    {
        private static ContentFile File => new("fixture.jar", new("https://cdn.modrinth.com/data/fixture.jar"), new string('a', 40), 10, true);
        public int InstallCalls, CacheCalls; public bool Unsupported, ServerOnly;
        public ContentInstallRequest? Installed; public ModrinthPackImportRequest? Imported;
        public Task<ContentInstallPlan> PlanContentInstallAsync(ContentInstallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContentInstallResult> InstallContentAsync(ContentInstallRequest request, CancellationToken cancellationToken = default)
        { InstallCalls++; Installed = request; return Task.FromResult(new ContentInstallResult(request.InstanceId, "/fixture/manifest.json", [new("project", "release", "mod", "mods/fixture.jar", File.Sha1, new string('b', 128), 10)], 1, [])); }
        public Task<ModrinthPackInspection> InspectModrinthPackAsync(string archivePath, CancellationToken cancellationToken = default) => Task.FromResult(new ModrinthPackInspection("Local pack", "1", "1.21.1", Unsupported ? "forge" : "vanilla", null, new string('c', 64),
            [new("mods/required.jar", File.Sha1, new string('b', 128), 10, [], "required"), new("mods/optional.jar", File.Sha1, new string('b', 128), 10, [], "optional"), new("server.jar", File.Sha1, new string('b', 128), 10, [], "unsupported")], [], [], !Unsupported, Unsupported ? "Forge导入尚未接入" : null));
        public Task<ModrinthPackImportResult> ImportModrinthPackAsync(ModrinthPackImportRequest request, CancellationToken cancellationToken = default)
        { Imported = request; return Task.FromResult(new ModrinthPackImportResult(new("imported", request.InstanceName, "1.21.1", "/fixture/imported"), 2, 1, 0)); }
        public Task<string> ExportModrinthPackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentProject>>([new("project", "project", "Fixture resource", "", "", 1, "mod", null, new("https://modrinth.com/mod/project"))]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([new("release", "Release", "1", DateTimeOffset.UtcNow, ["1.21.1"], ["fabric"], [File], Environment: ServerOnly ? "dedicated_server_only" : "unknown")]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) { CacheCalls++; return Task.FromResult("/fixture/fixture.jar"); }
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
