using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ResourceUpdateWorkspaceTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 200 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static MainWindowViewModel Open(Service service, string section)
    { var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); owner.Instances.ShowInstanceSettingsCommand.Execute(null); owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == section); return owner; }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task AutomaticCheckStaysInlineAndAResourceRowUpdatesDirectlyWithoutAModWarning(string section)
    {
        var service = new Service { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; using var owner = Open(service, section); var vm = owner.Instances;
        await Wait(() => service.Scans.Count == 1); Assert.False(owner.IsBusy); Assert.True(vm.CanImportContent); Assert.True(vm.IsModScanBusy); Assert.False(vm.CanUpdateContent);
        service.Gate.SetResult(); await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy);
        vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); Assert.Equal(2, vm.FilteredContentRows.Count); Assert.True(vm.CanUpdateContent); Assert.False(vm.CanManageMods);
        var row = vm.FilteredContentRows[0]; Assert.Equal("更新资源", row.UpdateCaption); row.DetailsCommand.Execute(null); Assert.Contains("sodium", vm.ModDetail); vm.CloseModDetailsCommand.Execute(null); await row.UpdateCommand.ExecuteAsync(null);
        Assert.Single(service.Applied); Assert.False(vm.HasModAction); Assert.Contains("1/1", vm.Feedback); await Wait(() => !vm.IsModScanBusy && vm.ModUpdates.Count == 1);
        Assert.Equal(section, service.Scans[0].Section); Assert.False(service.Scans[0].Refresh);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task BatchContinuesAfterOneFailureAndManualCheckBypassesCache(string section)
    {
        var service = new Service { FailFirst = true }; using var owner = Open(service, section); var vm = owner.Instances;
        await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); await vm.CheckModUpdatesCommand.ExecuteAsync(null); Assert.True(service.Scans.Last().Refresh);
        vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); vm.SelectAllModsCommand.Execute(null); await vm.UpdateSelectedModsCommand.ExecuteAsync(null);
        Assert.Equal(2, service.Applied.Count); Assert.False(vm.HasModAction); Assert.Contains("1/2", vm.Feedback); Assert.Contains("Alpha", vm.Feedback);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task AnEmptyUpdateFilterDoesNotClaimInstalledResourcesAreMissing(string section)
    {
        using var owner = Open(new Service(), section); var vm = owner.Instances; await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy);
        vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); vm.SelectAllModsCommand.Execute(null); await vm.UpdateSelectedModsCommand.ExecuteAsync(null); await Wait(() => !vm.IsModScanBusy && vm.ModUpdates.Count == 0);
        vm.ModFilters.Single(f => f.Key == "可更新").SelectCommand.Execute(null); Assert.True(vm.IsContentEmpty); Assert.Equal(3, vm.ContentEntries.Count); Assert.Equal("当前筛选下没有可更新的资源", vm.ContentEmptyTitle);
    }
    [AvaloniaFact]
    public async Task LatePageResultsAndOldRowCommandsCannotUpdateAnotherDirectory()
    {
        var service = new Service(); using var owner = Open(service, "resourcepacks"); var vm = owner.Instances; await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); var row = vm.FilteredContentRows[0];
        service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); var check = vm.CheckModUpdatesCommand.ExecuteAsync(null); await Wait(() => service.Scans.Count == 2); Assert.False(row.HasUpdate);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "shaders"); Assert.True(service.ReadToken.IsCancellationRequested); service.Gate.SetResult(); await check;
        await Wait(() => vm.FilteredContentRows.Count == 3 && !vm.IsModScanBusy); Assert.All(vm.FilteredContentRows, r => Assert.Equal("shaders", r.Entry.Kind));
        await row.UpdateCommand.ExecuteAsync(null); Assert.Empty(service.Applied);
        var current = vm.FilteredContentRows[0]; owner.SelectedPage = owner.Home; await current.UpdateCommand.ExecuteAsync(null); Assert.Empty(service.Applied);
    }
    [AvaloniaTheory]
    [InlineData("resourcepacks")]
    [InlineData("shaders")]
    public async Task MinimumWindowShowsAnEnabledInlineUpdateAndResourceBatchAction(string section)
    {
        using var owner = Open(new Service(), section); var vm = owner.Instances; var w = new MainWindow { Width = 880, Height = 560, DataContext = owner }; w.Show();
        try
        {
            await Wait(() => vm.ModUpdates.Count == 2 && !vm.IsModScanBusy); Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            Assert.Contains(w.GetVisualDescendants().OfType<Button>(), b => b.Name == "ModRowUpdate" && b.IsVisible && b.IsEnabled);
            vm.FilteredContentRows[0].Selected = true; Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
            var batch = w.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UpdateSelectedModsButton"); Assert.True(batch.IsVisible); Assert.True(batch.IsEnabled);
        }
        finally { w.Close(); }
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService, IInstanceResourceUpdateService
    {
        public TaskCompletionSource? Gate; public CancellationToken ReadToken; public List<(string Section, bool Refresh)> Scans = []; public List<string> Applied = []; public bool FailFirst; private HashSet<string> successful = [];
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default)
        {
            var folder = kind == "shaders" ? "shaderpacks" : kind;
            return Task.FromResult<IReadOnlyList<InstanceContentEntry>>(new[] { "Alpha", "Beta", "Gamma" }.Select(n => new InstanceContentEntry(n + ".zip", folder + "/" + n + ".zip", "/fixture/" + folder + "/" + n + ".zip", kind, 10, DateTimeOffset.UtcNow, ModName: n)).ToArray());
        }
        public async Task<InstanceModUpdateCheck> CheckInstanceResourceUpdatesAsync(string id, string kind, bool refresh = false, IProgress<PackInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Scans.Add((kind, refresh)); ReadToken = cancellationToken; if (Gate is not null) await Gate.Task;
            var rows = await ListInstanceContentAsync(id, kind); var updates = rows.Take(2).Where(r => !successful.Contains(r.RelativePath)).Select(r => new InstanceModUpdate(r.RelativePath, id, r.RelativePath, r.ModName!, "1", "2", 10)).ToArray();
            return new(updates, 3, [], rows.Select(r => new InstanceModOnlineInfo(r.RelativePath, new(r.ModName!, r.ModName!, r.ModName!, "Description", "Author", 1, kind == "shaders" ? "shader" : "resourcepack", null, new Uri("https://modrinth.com/resourcepack/fixture")), successful.Contains(r.RelativePath) ? "2" : "1", [new("required", "sodium")])).ToArray());
        }
        public Task<InstanceModUpdateResult> ApplyInstanceResourceUpdateAsync(string token, CancellationToken ct = default)
        { Applied.Add(token); if (FailFirst && token.EndsWith("Alpha.zip", StringComparison.Ordinal)) throw new InvalidDataException("fixture failed"); successful.Add(token); return Task.FromResult(new InstanceModUpdateResult("/fixture/backup", 1)); }
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportInstanceContentAsync(string id, string kind, string source, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("/fixture/export.zip");
    }
}
