using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WorldContentInstallationUiTests
{
    [AvaloniaTheory, InlineData("datapack"), InlineData("world")]
    public async Task WorldResourcesRequireExplicitTargetPreviewAndConfirmationInActualControls(string kind)
    {
        var service = new Service(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, service, Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r19", Guid.NewGuid() + ".json"));
        vm.Refresh(service.Snapshot); var window = new Window { Width = 880, Height = 560, Content = new DownloadsView { DataContext = vm } }; window.Show();
        try
        {
            await vm.OpenProjectAsync(new("project", "new-map", "public resource", "", "", 1, kind, null, new("https://modrinth.com/datapack/project")));
            Pump(window);
            var row = vm.ReleaseGroups.Single().Releases.Single().Files.Single(); Assert.True(row.ShowInstallButton); Assert.Equal("预检安装", row.InstallCaption);
            Assert.DoesNotContain("客户端不支持", vm.ReleaseGroups.Single().Releases.Single().Detail);
            Assert.True(Find<Border>(window, "WorldContentTargetPanel").IsEffectivelyVisible);
            if (kind == "datapack")
            {
                Assert.False(row.InstallCommand.CanExecute(null)); Assert.Null(vm.SelectedWorldContentTarget);
                Find<ComboBox>(window, "DatapackWorldTargetSelector").SelectedItem = vm.WorldContentTargets.First();
            }
            else { Find<TextBox>(window, "NewWorldDirectoryName").Text = "新世界 中文"; }
            Pump(window); Assert.True(row.InstallCommand.CanExecute(null));
            await row.InstallCommand.ExecuteAsync(null); Pump(window);
            Assert.True(vm.HasWorldContentPreview); Assert.Equal(0, service.Installs); Assert.Equal(1, service.Previews);
            Assert.True(Find<Button>(window, "ConfirmWorldContentInstallButton").IsEffectivelyEnabled);
            Assert.Equal(kind == "datapack" ? "world-a" : "新世界 中文", service.LastRequest!.WorldDirectoryName);
            if (kind == "datapack")
            {
                Find<ComboBox>(window, "DatapackWorldTargetSelector").SelectedItem = vm.WorldContentTargets.Last(); Pump(window);
                Assert.False(vm.HasWorldContentPreview); Assert.False(vm.ConfirmWorldContentInstallCommand.CanExecute(null));
                await row.InstallCommand.ExecuteAsync(null);
            }
            await vm.ConfirmWorldContentInstallCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal(1, service.Installs); Assert.False(vm.HasWorldContentPreview); Assert.Contains("已安装到", vm.WorldContentStatus);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CancellingPreviewOrFailedCommitDoesNotShowSuccessOrReuseStaleConfirmation()
    {
        var service = new Service(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, service, Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r19", Guid.NewGuid() + ".json")); vm.Refresh(service.Snapshot);
        await vm.OpenProjectAsync(new("project", "map", "resource", "", "", 1, "world", null, new("https://www.curseforge.com/minecraft/worlds/map")));
        var row = vm.ReleaseGroups.Single().Releases.Single().Files.Single(); await row.InstallCommand.ExecuteAsync(null);
        vm.CancelWorldContentPreviewCommand.Execute(null); Assert.False(vm.HasWorldContentPreview); Assert.Equal(0, service.Installs);
        await row.InstallCommand.ExecuteAsync(null); service.FailCommit = true; await vm.ConfirmWorldContentInstallCommand.ExecuteAsync(null);
        Assert.False(vm.HasWorldContentPreview); Assert.DoesNotContain("已安装", vm.WorldContentStatus); Assert.False(vm.ConfirmWorldContentInstallCommand.CanExecute(null));
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    [AvaloniaTheory, InlineData("datapack", "project", "datapack"), InlineData("datapack", "cf-1", null), InlineData("world", "cf-1", null)]
    public async Task WorldResourceFilesUseTheirOwnLoaderScopeAndOnlyZipCanBePreflighted(string kind, string projectId, string? expectedLoader)
    {
        var service = new Service { MixedFiles = true }; using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        using var vm = new DownloadsPageViewModel(owner, service, Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r19", Guid.NewGuid() + ".json"));
        vm.Refresh(service.Snapshot); vm.LoaderFilter = "Forge";
        await vm.OpenProjectAsync(new(projectId, "map", "resource", "", "", 1, kind, null, new("https://modrinth.com/datapack/project")));
        Assert.Equal(expectedLoader, service.LastLoader);
        if (kind == "datapack") vm.SelectedWorldContentTarget = vm.WorldContentTargets.First();
        var files = vm.ReleaseGroups.Single().Releases.Single().Files;
        Assert.True(files.Single(f => f.Name.EndsWith(".zip")).InstallCommand.CanExecute(null));
        Assert.False(files.Single(f => f.Name.EndsWith(".jar")).InstallCommand.CanExecute(null));
    }
    private sealed class Service : FakeLauncherService, IWorldContentInstallationService, ILauncherCatalogService
    {
        public int Previews, Installs; public bool FailCommit, MixedFiles; public string? LastLoader; public WorldContentRequest? LastRequest;
        public Task<IReadOnlyList<WorldContentTarget>> GetWorldContentTargetsAsync(string instanceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorldContentTarget>>([new("world-a", "世界甲", "1.21.1", 3955), new("world-b", "世界乙", "1.21.1", 3955)]);
        public Task<WorldContentPreview> PreviewWorldContentAsync(WorldContentRequest request, CancellationToken cancellationToken = default)
        { Previews++; LastRequest = request; return Task.FromResult(new WorldContentPreview("preview-" + Previews, request.Kind, "明确实例", request.WorldDirectoryName, "1.21.1", ["public.zip"], [])); }
        public Task<WorldContentResult> InstallWorldContentAsync(string previewId, CancellationToken cancellationToken = default)
        { Installs++; if (FailCommit) throw new IOException("controlled failure; original world preserved"); return Task.FromResult(new WorldContentResult(LastRequest!.InstanceId, LastRequest.WorldDirectoryName, 1, "/controlled")); }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default)
        {
            LastLoader = loader;
            ContentFile[] files = MixedFiles ? [new("public.zip", new("https://cdn.modrinth.com/public.zip"), new string('a', 40), 10), new("mod.jar", new("https://cdn.modrinth.com/mod.jar"), new string('b', 40), 20)] : [new("public.zip", new("https://cdn.modrinth.com/public.zip"), new string('a', 40), 10)];
            return Task.FromResult<IReadOnlyList<ContentRelease>>([new("release", "release", "1", DateTimeOffset.UnixEpoch, ["1.21.1"], ["datapack"], files, ProjectId: projectId, Environment: "server_only")]);
        }
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
