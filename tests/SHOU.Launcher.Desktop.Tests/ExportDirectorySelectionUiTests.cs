using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ExportDirectorySelectionUiTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 200 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    [AvaloniaTheory]
    [InlineData("cf")]
    [InlineData("mrpack")]
    public async Task SelectedDirectoriesAndFilesReachTheSharedExporterAndPersistAcrossNavigation(string format)
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher()); var vm = root.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "export");
        await Wait(() => vm.ExportEntries.Count == 3 && !vm.IsExportBrowseBusy); Assert.False(vm.ExportEntries.Single(r => r.Name == ".env").CanSelect);
        vm.ExportEntries.Single(r => r.Name == "kubejs").Selected = true;
        await vm.ExportEntries.Single(r => r.Name == "scripts").OpenCommand.ExecuteAsync(null); vm.ExportEntries.Single().Selected = true; Assert.Equal(2, vm.ExportSelectedPaths.Count);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "overview"); vm.SelectedSection = vm.Sections.Single(s => s.Key == "export"); Assert.Equal(2, vm.ExportSelectedPaths.Count);
        if (format == "cf") await vm.ExportCurseForgePackCommand.ExecuteAsync(null); else await vm.ExportModrinthPackCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "kubejs", "scripts/one.zs" }, service.Options!.OverridePaths); Assert.Equal(format, service.Format);
        vm.ExportSelectedPaths[0].RemoveCommand.Execute(null); Assert.Single(vm.ExportSelectedPaths); vm.ClearExportPathsCommand.Execute(null); Assert.Empty(vm.ExportSelectedPaths);
    }
    [AvaloniaFact]
    public async Task InstanceSwitchDropsOldSelectionsAndLateDirectoryResults()
    {
        var service = new Service(); service.Publish(service.Snapshot with { Instances = [.. service.Snapshot.Instances, new("other", "Other", "1.21.1", "/fixture/other")] });
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher()); var vm = root.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "export"); await Wait(() => vm.ExportEntries.Count == 3 && !vm.IsExportBrowseBusy);
        vm.ExportEntries[0].Selected = true; var oldRow = vm.ExportEntries[0]; service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); var pending = vm.RefreshExportEntriesCommand.ExecuteAsync(null);
        vm.SelectedInstance = vm.Instances.Single(i => i.Id == "other"); service.Gate.SetResult(); await pending; await Wait(() => !vm.IsExportBrowseBusy);
        Assert.Empty(vm.ExportSelectedPaths); oldRow.Selected = false; oldRow.Selected = true; Assert.Empty(vm.ExportSelectedPaths);
    }
    private sealed class Service : FakeLauncherService, IInstanceExportSelectionService, ICurseForgeExportService, IModrinthPackService
    {
        public ModrinthPackExportOptions? Options; public string? Format; public TaskCompletionSource? Gate;
        public async Task<IReadOnlyList<InstanceExportEntry>> GetInstanceExportEntriesAsync(string id, string directory = "", CancellationToken cancellationToken = default)
        { if (Gate is not null) await Gate.Task; return directory == "" ? [new("kubejs", true, 0), new("scripts", true, 0), new(".env", false, 1, false, "fixture credentials")] : [new("scripts/one.zs", false, 1)]; }
        public Task<string> ExportCurseForgePackAsync(string id, ModrinthPackExportOptions options, CancellationToken ct = default) { Options = options; Format = "cf"; return Task.FromResult("fixture.zip"); }
        public Task<string> ExportModrinthPackAsync(string id, ModrinthPackExportOptions options, CancellationToken ct = default) { Options = options; Format = "mrpack"; return Task.FromResult("fixture.mrpack"); }
        public Task<ModrinthPackInspection> InspectModrinthPackAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ModrinthPackImportResult> ImportModrinthPackAsync(ModrinthPackImportRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
