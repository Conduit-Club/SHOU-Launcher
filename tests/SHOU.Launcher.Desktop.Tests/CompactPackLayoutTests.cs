using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CompactPackLayoutTests
{
    [AvaloniaTheory]
    [InlineData("mods", "Fabric")]
    [InlineData("resourcepacks", "任意")]
    [InlineData("shaders", "任意")]
    public void InstanceBrowserPinsGameAndTargetWithoutFilteringTexturesByModLoader(string section, string expectedLoader)
    {
        var service = new Catalog(); var instance = service.Snapshot.Instances[0] with { Loader = "fabric", LoaderVersion = "0.18.4", GameVersionId = "1.21.1" };
        service.Publish(service.Snapshot with { Instances = [instance] });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        vm.Downloads.OpenInstanceResourceBrowser(instance, section);
        Assert.Equal("1.21.1", vm.Downloads.GameVersionFilter); Assert.Equal(expectedLoader, vm.Downloads.LoaderFilter);
        Assert.Equal(instance.Id, vm.Downloads.SelectedInstallTarget!.Instance.Id);
    }
    [AvaloniaFact]
    public async Task LocalInstallStaysBySearchAndDoesNotOccupyAnEmptyDetailCard()
    {
        using var vm = new MainWindowViewModel(new Catalog(), new FakeExternalLauncher());
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 560 }; window.Show();
        try
        {
            vm.ShowDownloadsCommand.Execute(null); vm.Downloads.SelectCategory("modpack"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
            var local = Assert.Single(controls.OfType<Button>(), b => b.Name == "ReadModrinthPackButton");
            var search = Assert.Single(controls.OfType<Button>(), b => b.Name == "SearchContentButton");
            Assert.True(local.IsEffectivelyVisible); Assert.Same(search.Parent, local.Parent); Assert.False(vm.Downloads.ShowLocalPackPanel);
            await vm.Downloads.OpenProjectAsync(new("fixture", "fixture", "Fixture", "", "", 0, "modpack", null, new("https://modrinth.com/modpack/fixture")));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.False(vm.Downloads.ShowLocalPackPanel); Assert.False(local.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
    private sealed class Catalog : FakeLauncherService, ILauncherCatalogService
    {
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
