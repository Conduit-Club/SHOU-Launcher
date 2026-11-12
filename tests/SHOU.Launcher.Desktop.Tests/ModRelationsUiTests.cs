using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ModRelationsUiTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 200 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static MainWindowViewModel Open(Service service)
    {
        var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); owner.Instances.ShowInstanceSettingsCommand.Execute(null);
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "relations"); return owner;
    }
    private static ModDeclaration Node(string path, string id, IReadOnlyList<ModDeclaredDependency>? dependencies = null, IReadOnlyList<ModDeclaration>? children = null)
        => new(path, id, "1.0", "fabric", [id], dependencies ?? [], children ?? [], []);
    private static InstanceModRelations Report() => ModRelations.Build([
        new("mods/host.jar", true, Node("mods/host.jar", "Host", children: [Node("mods/host.jar!/inner.jar", "deep_id")])),
        new("mods/consumer.jar", true, Node("mods/consumer.jar", "Consumer", [new("deep_id", ">=1")])),
        new("mods/disabled.jar.disabled", false, Node("mods/disabled.jar.disabled", "disabled_id")),
        new("mods/needs.jar", true, Node("mods/needs.jar", "Needs review", [new("disabled_id", "*"), new("optional_missing", "*", true)]))]);
    [AvaloniaFact]
    public async Task EnteringPageReadsWithoutBlockingAndSearchFindsEmbeddedIdsAndDependencySources()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Reads == 1); Assert.False(owner.IsBusy); Assert.True(vm.IsModRelationsBusy);
        service.Gate.SetResult(); await Wait(() => vm.FilteredModRelationHosts.Count == 4 && !vm.IsModRelationsBusy);
        vm.ModRelationSearch = "deep_id"; Assert.Equal(2, vm.FilteredModRelationHosts.Count); Assert.All(vm.FilteredModRelationHosts, h => Assert.Contains("deep_id", h.Search));
        vm.ModRelationSearch = ""; vm.ModRelationIssuesOnly = true; Assert.Single(vm.FilteredModRelationHosts); Assert.Equal("Needs review", vm.SelectedModRelationHost!.Name);
        vm.ModRelationSearch = "not present"; Assert.Empty(vm.FilteredModRelationHosts); Assert.Null(vm.SelectedModRelationHost); Assert.True(vm.ModRelationsEmpty);
    }
    [AvaloniaFact]
    public async Task LeavingPageAndChangingInstanceRejectLateReadAndDisposeCancels()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Reads == 1); vm.BackToModsCommand.Execute(null); Assert.True(service.ReadToken.IsCancellationRequested);
        service.Gate.SetResult(); await Wait(() => !vm.IsModRelationsBusy); Assert.Empty(vm.FilteredModRelationHosts);
        service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); vm.SelectedSection = vm.Sections.Single(s => s.Key == "relations");
        await Wait(() => service.Reads == 2); vm.SelectedInstance = vm.SelectedInstance! with { Id = "different", GameDirectory = "/fixture/different" };
        Assert.True(service.ReadToken.IsCancellationRequested); owner.Dispose(); service.Gate.SetResult(); await Task.Delay(15); Dispatcher.UIThread.RunJobs(); Assert.Empty(vm.FilteredModRelationHosts);
    }
    [AvaloniaFact]
    public async Task CancelledReadCanBeRetriedWithoutReusingItsLateResult()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Reads == 1); vm.CancelModRelationsCommand.Execute(null); Assert.True(service.ReadToken.IsCancellationRequested); Assert.False(vm.IsModRelationsBusy);
        service.Gate.SetResult(); await Wait(() => !vm.RefreshModRelationsCommand.IsRunning); Assert.Empty(vm.FilteredModRelationHosts);
        service.Gate = null; await vm.RefreshModRelationsCommand.ExecuteAsync(null); Assert.Equal(4, vm.FilteredModRelationHosts.Count);
    }
    [AvaloniaFact]
    public async Task ModMutationWaitsForCancelledRelationshipReaderToClose()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Reads == 1); vm.BackToModsCommand.Execute(null); await Wait(() => vm.ContentEntries.Count == 1 && !owner.IsBusy);
        vm.FilteredContentRows.Single().Selected = true; var write = vm.DisableSelectedModsCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Writes); Assert.True(service.ReadToken.IsCancellationRequested);
        service.Gate.SetResult(); await write; Assert.Equal(1, service.Writes); Assert.True(service.ReaderReturned);
    }
    [AvaloniaFact]
    public async Task CompactPageShowsSearchSelectorAndSelectedDeclarationsAndCheckButtonNavigatesHere()
    {
        using var owner = Open(new Service()); var vm = owner.Instances;
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            await Wait(() => !vm.IsModRelationsBusy && vm.FilteredModRelationHosts.Count == 4); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
            var search = controls.OfType<TextBox>().Single(c => c.Name == "ModRelationsSearch"); var selector = controls.OfType<ComboBox>().Single(c => c.Name == "ModRelationHostSelector");
            Assert.True(search.TranslatePoint(default, window)!.Value.Y > 0); Assert.True(selector.TranslatePoint(default, window)!.Value.Y + selector.Bounds.Height < window.ClientSize.Height);
            vm.ModRelationSearch = "Consumer"; Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.Single(vm.SelectedModRelationHost!.Dependencies);
            Assert.Equal(ModDeclarationPresence.Embedded, Report().Dependencies.First().Presence);
            vm.BackToModsCommand.Execute(null); vm.DiagnoseModsCommand.Execute(null); Assert.True(vm.IsRelationsSection);
        }
        finally { window.Close(); }
    }
    private sealed class Service : FakeLauncherService, IInstanceModRelationsService, IInstanceWorkspaceService, IInstanceModManagementService
    {
        public TaskCompletionSource? Gate; public int Reads, Writes; public bool ReaderReturned; public CancellationToken ReadToken;
        public async Task<InstanceModRelations> ReadInstanceModRelationsAsync(string id, CancellationToken ct = default)
        { Reads++; ReadToken = ct; if (Gate is not null) await Gate.Task; ReaderReturned = true; return Report(); }
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceContentEntry>>([new("one.jar", "mods/one.jar", "/fixture/one.jar", "mods", 1, DateTimeOffset.UtcNow)]);
        public Task ImportInstanceContentAsync(string id, string kind, string source, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("fixture.zip");
        public Task SetInstanceModsEnabledAsync(string id, IReadOnlyList<string> paths, bool enabled, CancellationToken ct = default) { Assert.True(ReaderReturned); Writes++; return Task.CompletedTask; }
        public Task<InstanceModTrash> TrashInstanceModsAsync(string id, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstanceModTrash>> GetInstanceModTrashAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceModTrash>>([]);
        public Task RestoreInstanceModsAsync(string id, string trash, CancellationToken ct = default) => Task.CompletedTask;
    }
}
