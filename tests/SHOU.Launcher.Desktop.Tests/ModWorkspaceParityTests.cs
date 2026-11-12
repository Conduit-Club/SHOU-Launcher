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

public sealed class ModWorkspaceParityTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 100 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static MainWindowViewModel Open(Service service)
    {
        var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); owner.Instances.ShowInstanceSettingsCommand.Execute(null);
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "mods"); return owner;
    }
    [AvaloniaFact]
    public async Task EnteringModsAutomaticallyChecksWithoutBlockingAndFiltersUpdateTheOriginalList()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Scans > 0); Assert.False(owner.IsBusy); Assert.True(vm.IsModScanBusy); Assert.True(vm.CanImportContent);
        service.Gate.SetResult(); await Wait(() => !vm.IsModScanBusy && vm.ModUpdates.Count == 2);
        vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); Assert.Equal(2, vm.FilteredContentRows.Count);
        vm.ContentSearchText = "Alpha"; Assert.Single(vm.FilteredContentRows); vm.ContentSearchText = "";
        vm.ModFilters.Single(f => f.Key == "禁用").SelectCommand.Execute(null); Assert.Single(vm.FilteredContentRows);
        vm.ModFilters.Single(f => f.Key == "重复").SelectCommand.Execute(null); Assert.Equal(2, vm.FilteredContentRows.Count);
        vm.ModFilters.Single(f => f.Key == "错误").SelectCommand.Execute(null); Assert.Single(vm.FilteredContentRows); Assert.Empty(service.Applied);
    }
    [AvaloniaFact]
    public async Task NavigatingAwayDiscardsLateResultsAndProgress()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Scans > 0); owner.SelectedPage = owner.Home; service.Gate.SetResult(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.ModUpdates); Assert.False(vm.IsModScanBusy); Assert.Empty(service.Applied);
    }
    [AvaloniaFact]
    public async Task BatchUpdatesUseOnePromptContinuePastFailureAndDoNotApplyBeforeConfirmation()
    {
        var service = new Service { FailFirst = true }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); vm.SelectAllModsCommand.Execute(null);
        vm.UpdateSelectedModsCommand.Execute(null); Assert.True(vm.HasModAction); Assert.Empty(service.Applied);
        await vm.ConfirmModActionCommand.ExecuteAsync(null); Assert.Equal(2, service.Applied.Count); Assert.Contains("1/2", vm.Feedback); Assert.Contains("Alpha", vm.Feedback);
    }
    [AvaloniaFact]
    public async Task BatchDisableTrashCancelAndConfirmedRestoreUseExactSelection()
    {
        var service = new Service(); using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); vm.FilteredContentRows[0].Selected = true;
        await vm.DisableSelectedModsCommand.ExecuteAsync(null); await Wait(() => !vm.IsModScanBusy);
        Assert.Single(service.Toggled); var row = vm.FilteredContentRows.Single(r => r.Entry.RelativePath == service.Toggled[0]); row.Selected = true;
        vm.TrashSelectedModsCommand.Execute(null); vm.CancelModActionCommand.Execute(null); Assert.Empty(service.Trashed);
        vm.TrashSelectedModsCommand.Execute(null); row.Selected = false; await vm.ConfirmModActionCommand.ExecuteAsync(null); Assert.Single(service.Trashed);
        await Wait(() => !vm.IsModScanBusy); await vm.RestoreModsCommand.ExecuteAsync(null); Assert.Equal("trash", service.Restored);
    }
    [AvaloniaFact]
    public async Task CompactWindowHasFiltersRowActionsDetailsAndSortingWithoutOldConfirmationCheckbox()
    {
        var service = new Service { Notices = ["A long unresolved project notice.", "Another unresolved project notice.", "A third unresolved project notice."] }; using var owner = Open(service); var vm = owner.Instances; var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<ItemsControl>(), c => c.Name == "InstanceModFilters");
            var notices = window.GetVisualDescendants().OfType<Expander>().Single(c => c.Name == "ModUpdateNotices");
            Assert.False(notices.IsExpanded); Assert.True(vm.HasModUpdateDetails); Assert.DoesNotContain('\n', vm.ModUpdateSummary);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "InstanceContentList");
            var listTop = list.TranslatePoint(default, window)!.Value.Y;
            Assert.True(listTop + 74 < window.ClientSize.Height, $"The first mod row must remain visible at 880x560; list top: {listTop}.");
            notices.IsExpanded = true; window.UpdateLayout(); Assert.True(notices.Bounds.Height > 50); notices.IsExpanded = false;
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "ConfirmInstanceModUpdate");
            var row = vm.FilteredContentRows.First(r => r.HasUpdate); row.DetailsCommand.Execute(null); Assert.Contains("ID:", vm.ModDetail);
            row.UpdateCommand.Execute(null); Assert.True(vm.HasModAction); Assert.Empty(service.Applied);
            vm.CancelModActionCommand.Execute(null); vm.ContentSort = "文件名"; Assert.Equal(vm.FilteredContentRows.Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase), vm.FilteredContentRows.Select(r => r.Name));
            Assert.Contains("Name,Version,File,State", vm.ExportContentInformation(true));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CancelThenLateCompletionDoesNotRepopulateRowsAndDisposeCancelsTheRead()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Scans > 0); vm.CancelModScanCommand.Execute(null); Assert.Contains("已取消", vm.ModUpdateStatus); owner.Dispose(); service.Gate.SetResult(); await Task.Delay(20); Dispatcher.UIThread.RunJobs(); Assert.Empty(vm.ModUpdates);
    }
    [AvaloniaFact]
    public async Task AWriteWaitsForCancelledReadersToReleaseTheirFiles()
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service); var vm = owner.Instances;
        await Wait(() => service.Scans > 0); vm.CancelModScanCommand.Execute(null); vm.FilteredContentRows[0].Selected = true;
        var write = vm.DisableSelectedModsCommand.ExecuteAsync(null); Assert.True(owner.IsBusy); Assert.Empty(service.Toggled);
        service.Gate.SetResult(); await write; Assert.Single(service.Toggled);
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService, IInstanceModUpdateService, IInstanceModManagementService
    {
        public TaskCompletionSource? Gate; public int Scans; public bool FailFirst; public List<string> Applied = [], Toggled = [], Trashed = []; public string? Restored; public IReadOnlyList<string> Notices = [];
        private IReadOnlyList<InstanceContentEntry> rows;
        public Service()
        {
            var i = Snapshot.Instances.Single() with { Loader = "fabric", GameVersionId = "1.21.1" }; Publish(Snapshot with { Instances = [i] });
            rows = [new("alpha.jar", "mods/alpha.jar", "/fixture/alpha.jar", "mods", 10, DateTimeOffset.UtcNow, ModName: "Alpha", ModVersion: "1", ModId: "same"),
                new("beta.jar", "mods/beta.jar", "/fixture/beta.jar", "mods", 20, DateTimeOffset.UtcNow, Enabled: false, ModName: "Beta", ModVersion: "1", ModId: "same"),
                new("broken.jar", "mods/broken.jar", "/fixture/broken.jar", "mods", 1, DateTimeOffset.UtcNow, MetadataError: "broken archive")];
        }
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Task.FromResult(rows);
        public Task ImportInstanceContentAsync(string id, string kind, string source, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("/fixture/export.zip");
        public async Task<InstanceModUpdateCheck> CheckInstanceModUpdatesAsync(string id, CancellationToken ct = default)
        {
            Scans++; if (Gate is not null) await Gate.Task; return new(rows.Take(2).Where(r => !Applied.Contains(r.Name)).Select(r => new InstanceModUpdate(r.Name, id, r.RelativePath, r.ModName!, "1", "2", 10)).ToArray(), 3, Notices,
                [new("mods/alpha.jar", new("project", "alpha", "Alpha online", "Description", "Author", 1, "mod", null, new Uri("https://modrinth.com/mod/alpha")))]);
        }
        public Task<InstanceModUpdateResult> ApplyInstanceModUpdateAsync(string token, CancellationToken ct = default)
        { Applied.Add(token); if (FailFirst && token == "alpha.jar") throw new IOException("fixture failure"); return Task.FromResult(new InstanceModUpdateResult("/fixture/backup", 1)); }
        public Task SetInstanceModsEnabledAsync(string id, IReadOnlyList<string> paths, bool enabled, CancellationToken ct = default) { Toggled.AddRange(paths); rows = rows.Select(r => paths.Contains(r.RelativePath) ? r with { Enabled = enabled } : r).ToArray(); return Task.CompletedTask; }
        public Task<InstanceModTrash> TrashInstanceModsAsync(string id, IReadOnlyList<string> paths, CancellationToken ct = default) { Trashed.AddRange(paths); return Task.FromResult(new InstanceModTrash("trash", paths.Count, DateTimeOffset.UtcNow)); }
        public Task<IReadOnlyList<InstanceModTrash>> GetInstanceModTrashAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceModTrash>>(Trashed.Count > 0 ? [new("trash", Trashed.Count, DateTimeOffset.UtcNow)] : []);
        public Task RestoreInstanceModsAsync(string id, string trashId, CancellationToken ct = default) { Restored = trashId; return Task.CompletedTask; }
    }
}
