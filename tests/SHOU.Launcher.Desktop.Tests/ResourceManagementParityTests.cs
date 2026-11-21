using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ResourceManagementParityTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 200 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static MainWindowViewModel Open(Service service, string kind)
    { var root = new MainWindowViewModel(service, new FakeExternalLauncher()); root.Instances.ShowInstanceSettingsCommand.Execute(null); root.Instances.SelectedSection = root.Instances.Sections.Single(s => s.Key == kind); return root; }

    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    [InlineData("schematics")]
    public async Task SingleAndBatchTrashUseCapturedFilesAndRestoreOnlyTheirCategory(string kind)
    {
        var service = new Service(); using var root = Open(service, kind); var vm = root.Instances; await Wait(() => vm.FilteredContentRows.Count == 3 && !vm.IsModScanBusy);
        var row = vm.FilteredContentRows[0]; vm.FilteredContentRows[1].Selected = true;
        row.TrashCommand.Execute(null); vm.CancelModActionCommand.Execute(null); Assert.Empty(service.Trashed);
        row.TrashCommand.Execute(null); Assert.Contains(row.Entry.RelativePath, vm.ModActionSummary); Assert.DoesNotContain(vm.FilteredContentRows[1].Entry.RelativePath, vm.ModActionSummary);
        await vm.ConfirmModActionCommand.ExecuteAsync(null); Assert.Equal(new[] { row.Entry.RelativePath }, service.Trashed[0].Paths); Assert.Equal(kind, service.Trashed[0].Kind);
        await Wait(() => vm.ContentEntries.Count == 2 && !vm.IsModScanBusy); Assert.True(vm.HasModTrash);
        vm.CloseContentTrashCommand.Execute(null); Assert.False(vm.HasModTrash); await vm.RefreshModTrashCommand.ExecuteAsync(null); Assert.True(vm.HasModTrash);
        await vm.RestoreModsCommand.ExecuteAsync(null); await Wait(() => vm.ContentEntries.Count == 3 && !vm.IsModScanBusy); Assert.False(vm.HasModTrash); Assert.Equal(kind, service.Restored.Single());
        vm.SelectAllModsCommand.Execute(null); vm.TrashSelectedModsCommand.Execute(null); vm.ClearModSelectionCommand.Execute(null); await vm.ConfirmModActionCommand.ExecuteAsync(null);
        Assert.Equal(3, service.Trashed.Last().Paths.Count); Assert.Equal(kind, service.Trashed.Last().Kind);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task DeletionCancelsAndDrainsMetadataReadersBeforeMovingFiles(string kind)
    {
        var service = new Service { MetadataGate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var root = Open(service, kind); var vm = root.Instances;
        await Wait(() => service.Reads == 1 && vm.FilteredContentRows.Count == 3); Assert.True(vm.CanTrashContent);
        vm.FilteredContentRows[0].TrashCommand.Execute(null); var deletion = vm.ConfirmModActionCommand.ExecuteAsync(null);
        await Wait(() => service.MetadataToken.IsCancellationRequested); Assert.Empty(service.Trashed);
        service.MetadataGate.SetResult(); await deletion; Assert.Single(service.Trashed);
    }
    [AvaloniaFact]
    public async Task LateTrashResultsAndOldRowsCannotAffectAnotherSection()
    {
        var service = new Service(); using var root = Open(service, "resourcepacks"); var vm = root.Instances; await Wait(() => vm.FilteredContentRows.Count == 3 && !vm.IsModScanBusy); var oldRow = vm.FilteredContentRows[0];
        oldRow.TrashCommand.Execute(null); await vm.ConfirmModActionCommand.ExecuteAsync(null); Assert.True(vm.HasModTrash);
        service.TrashGate = new(TaskCreationOptions.RunContinuationsAsynchronously); var read = vm.RefreshModTrashCommand.ExecuteAsync(null); await Wait(() => service.TrashReading);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "shaders"); service.TrashGate.SetResult(); await read; await Wait(() => vm.FilteredContentRows.Count == 3 && !vm.IsModScanBusy);
        Assert.False(vm.HasModTrash); Assert.False(vm.HasModAction); oldRow.TrashCommand.Execute(null); Assert.False(vm.HasModAction); Assert.Single(service.Trashed);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    [InlineData("schematics")]
    public async Task ResourceRowsExposeDeleteWithoutInventingEnableControlsAndTrashPanelStaysCompact(string kind)
    {
        using var root = Open(new Service(), kind); var vm = root.Instances; var w = new MainWindow { Width = 880, Height = 560, DataContext = root }; w.Show();
        try
        {
            await Wait(() => vm.FilteredContentRows.Count == 3 && !vm.IsModScanBusy); Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "ContentRowTrashButton" && b.IsVisible && b.IsEnabled);
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "ContentRowToggleButton" && b.IsVisible);
            vm.FilteredContentRows[0].TrashCommand.Execute(null); await vm.ConfirmModActionCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            var panel = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ContentTrashPanel"); Assert.True(panel.IsVisible); Assert.InRange(panel.Bounds.Height, 40, 80);
            Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "RestoreModsButton" && b.IsVisible && b.IsEnabled);
        }
        finally { w.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task VerifiedLocalDetailsOpenVersionPageWithExactTargetAndReturnToOriginalSearch(string kind)
    {
        var service = new Service(); using var root = Open(service, kind); var vm = root.Instances; await Wait(() => vm.FilteredContentRows.Any(r => r.Project is not null) && !vm.IsModScanBusy);
        vm.ContentSearchText = "Alpha"; vm.FilteredContentRows.Single().DetailsCommand.Execute(null); Assert.True(vm.CanOpenContentProject);
        await vm.OpenContentProjectCommand.ExecuteAsync(null); Assert.Same(root.Downloads, root.SelectedPage); Assert.Equal("verified", root.Downloads.SelectedProject?.Id);
        Assert.Equal(vm.SelectedInstance!.Id, root.Downloads.SelectedInstallTarget?.Instance.Id); Assert.Equal("1.21.1", service.ReleaseQuery.Game); Assert.Null(service.ReleaseQuery.Loader); Assert.True(root.Downloads.HasReleases);
        root.BackCommand.Execute(null); Assert.Same(vm, root.SelectedPage); Assert.Equal(kind, vm.SelectedSection.Key); Assert.Equal("Alpha", vm.ContentSearchText); Assert.Empty(service.Trashed); Assert.Equal(0, service.Downloads);
        await Wait(() => !vm.IsModScanBusy); vm.ContentSearchText = "Unknown"; vm.FilteredContentRows.Single().DetailsCommand.Execute(null); Assert.False(vm.CanOpenContentProject);
    }
    [AvaloniaFact]
    public async Task AnUnrelatedProjectOpenDoesNotInheritAnOldInstanceReturn()
    {
        var service = new Service(); using var root = Open(service, "resourcepacks"); var vm = root.Instances; await Wait(() => vm.FilteredContentRows.Any(r => r.Project is not null) && !vm.IsModScanBusy);
        vm.FilteredContentRows[0].DetailsCommand.Execute(null); await vm.OpenContentProjectCommand.ExecuteAsync(null);
        await root.Downloads.OpenProjectAsync(Service.Project with { Id = "other" }); root.BackCommand.Execute(null); Assert.Same(root.Downloads, root.SelectedPage);
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService, IInstanceContentTrashService, IInstanceContentMetadataService, ILauncherCatalogService
    {
        public static ContentProject Project => new("verified", "verified", "Alpha", "Verified description", "Author", 1, "resourcepack", null, new("https://modrinth.com/resourcepack/verified"));
        public List<(string Kind, IReadOnlyList<string> Paths)> Trashed = []; public List<string> Restored = []; private Dictionary<string, IReadOnlyList<string>> trash = [];
        public TaskCompletionSource? MetadataGate, TrashGate; public CancellationToken MetadataToken; public int Reads, Downloads; public bool TrashReading; public (string? Game, string? Loader) ReleaseQuery;
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default)
        {
            var folder = kind == "shaders" ? "shaderpacks" : kind; var extension = kind == "schematics" ? ".litematic" : ".zip";
            return Task.FromResult<IReadOnlyList<InstanceContentEntry>>(new[] { "Alpha", "Beta", "Unknown" }.Select(n => new InstanceContentEntry(n + extension, folder + "/" + n + extension, "/fixture/" + folder + "/" + n + extension, kind, 10, DateTimeOffset.UtcNow)).Where(e => !trash.GetValueOrDefault(kind, []).Contains(e.RelativePath)).ToArray());
        }
        public async Task<InstanceContentInfoCheck> ReadInstanceContentMetadataAsync(string id, string kind, CancellationToken ct = default)
        { Reads++; MetadataToken = ct; if (MetadataGate is not null) await MetadataGate.Task; ct.ThrowIfCancellationRequested(); var rows = await ListInstanceContentAsync(id, kind); return new(rows.Take(2).Select(r => new InstanceContentOnlineInfo(r.RelativePath, Project with { Title = Path.GetFileNameWithoutExtension(r.Name), Kind = kind == "shaders" ? "shader" : "resourcepack" }, "1")).ToArray(), []); }
        public Task<InstanceModTrash> TrashInstanceContentAsync(string id, string kind, IReadOnlyList<string> paths, CancellationToken ct = default)
        { Assert.True(MetadataGate is null || MetadataGate.Task.IsCompleted); Trashed.Add((kind, paths)); trash[kind] = paths; return Task.FromResult(new InstanceModTrash(kind, paths.Count, DateTimeOffset.UtcNow)); }
        public async Task<IReadOnlyList<InstanceModTrash>> GetInstanceContentTrashAsync(string id, string kind, CancellationToken ct = default)
        { var rows = trash.TryGetValue(kind, out var paths) ? new[] { new InstanceModTrash(kind, paths.Count, DateTimeOffset.UtcNow) } : []; TrashReading = true; if (TrashGate is not null) await TrashGate.Task; return rows; }
        public Task RestoreInstanceContentAsync(string id, string kind, string trashId, CancellationToken ct = default) { Assert.Equal(kind, trashId); Restored.Add(kind); trash.Remove(kind); return Task.CompletedTask; }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string game, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string id, string? game = null, string? loader = null, CancellationToken cancellationToken = default)
        { ReleaseQuery = (game, loader); return Task.FromResult<IReadOnlyList<ContentRelease>>([new("release", "Fixture", "1", DateTimeOffset.UtcNow, ["1.21.1"], [], [])]); }
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) { Downloads++; return Task.FromResult("/fixture/download"); }
        public Task<byte[]> GetContentIconAsync(string icon, CancellationToken ct = default) => Task.FromResult<byte[]>([]);
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportInstanceContentAsync(string id, string kind, string source, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("/fixture/export.zip");
    }
}
