using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class SchematicDirectoryUiTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 300 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    private static MainWindowViewModel Open(Service service)
    { var root = new MainWindowViewModel(service, new FakeExternalLauncher()); root.Instances.ShowInstanceSettingsCommand.Execute(null); root.Instances.SelectedSection = root.Instances.Sections.Single(s => s.Key == "schematics"); return root; }
    [AvaloniaFact]
    public async Task FolderNavigationSortsFoldersFirstImportsToThatFolderAndDropsOldRows()
    {
        var service = new Service(); using var root = Open(service); var vm = root.Instances; await Wait(() => vm.ContentEntries.Count == 2 && !root.IsBusy);
        var folder = vm.FilteredContentRows.First(); Assert.True(folder.IsDirectory); vm.SelectAllModsCommand.Execute(null); Assert.True(vm.HasModSelection); Assert.False(folder.Selected); vm.SelectAllModsCommand.Execute(null); Assert.False(vm.HasModSelection);
        await vm.OpenContentFolderAsync(folder); Assert.Equal("builds", vm.ContentDirectory); Assert.True(vm.ParentContentDirectoryCommand.CanExecute(null)); Assert.Single(vm.ContentEntries);
        await vm.ImportContentFileAsync("/fixture/source.schem"); Assert.Equal("builds", service.ImportedDirectory); Assert.Equal(0, service.RootImports);
        var child = vm.FilteredContentRows.Single(); await vm.ParentContentDirectoryCommand.ExecuteAsync(null); Assert.Equal("", vm.ContentDirectory); vm.ToggleContentSelection(child); Assert.False(child.Selected);
        await vm.OpenContentFolderAsync(folder); Assert.Equal("", vm.ContentDirectory); // stale row cannot navigate
        await vm.OpenContentFolderAsync(vm.FilteredContentRows.First()); vm.SelectedSection = vm.Sections.Single(s => s.Key == "overview"); Assert.Equal("", vm.ContentDirectory);
    }
    [AvaloniaFact]
    public async Task LateDirectoryReadCannotPopulateAnotherPageAndAStalePickerCannotImport()
    {
        var service = new Service(); using var root = Open(service); var vm = root.Instances; await Wait(() => vm.ContentEntries.Count == 2 && !root.IsBusy);
        service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously); var read = vm.OpenContentFolderAsync(vm.FilteredContentRows.First()); await Wait(() => service.LastDirectory == "builds");
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "overview"); service.Gate.SetResult(); await read; Assert.Empty(vm.ContentEntries); Assert.Equal("", vm.ContentDirectory);
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "schematics"); await Wait(() => vm.ContentEntries.Count == 2 && !root.IsBusy);
        await vm.ImportDroppedContentAsync(vm.SelectedInstance!, "schematics", ["/fixture/source.schem"], "old-folder"); Assert.Null(service.ImportedDirectory); Assert.Equal(0, service.RootImports);
    }
    [AvaloniaFact]
    public async Task EnterBackspaceAndDoublePointerClickNavigateThroughTheActualView()
    {
        using var root = Open(new Service()); var vm = root.Instances; var window = new MainWindow { Width = 976, Height = 636, DataContext = root }; window.Show();
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        try
        {
            await Wait(() => vm.ContentEntries.Count == 2 && !root.IsBusy); Pump(); var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "InstanceContentList");
            list.SelectedItem = vm.FilteredContentRows.First(); Assert.True(list.ContainerFromIndex(0)!.Focus()); Assert.True(vm.SelectedContentRow?.IsDirectory); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Wait(() => vm.ContentDirectory == "builds" && !root.IsBusy); Pump();
            Assert.True(list.ContainerFromIndex(0)!.Focus()); window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null); await Wait(() => vm.ContentDirectory == "" && !root.IsBusy); Pump();
            var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("resourceRow") && b.DataContext is InstanceContentRowViewModel { IsDirectory: true });
            var point = row.TranslatePoint(new Point(130, row.Bounds.Height / 2), window)!.Value; window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await Wait(() => vm.ContentDirectory == "builds" && !root.IsBusy); Assert.False(vm.HasModSelection);
        }
        finally { window.Close(); }
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService, IInstanceContentBrowserService
    {
        public TaskCompletionSource? Gate; public string? LastDirectory, ImportedDirectory; public int RootImports;
        private static InstanceContentEntry Entry(string name, bool folder = false) => new(Path.GetFileName(name), "schematics/" + name, "/fixture/schematics/" + name, "schematics", 1, DateTimeOffset.UtcNow, Directory: folder);
        public async Task<IReadOnlyList<InstanceContentEntry>> BrowseInstanceContentAsync(string id, string kind, string directory, CancellationToken ct = default)
        { LastDirectory = directory; if (Gate is not null) await Gate.Task; return directory == "" ? [Entry("a.schem"), Entry("builds", true)] : [Entry("builds/a.schem")]; }
        public Task<string> GetInstanceContentDirectoryAsync(string id, string kind, string directory, CancellationToken ct = default) => Task.FromResult("/fixture/schematics/" + directory);
        public Task ImportInstanceContentToDirectoryAsync(string id, string kind, string directory, string path, CancellationToken ct = default) { ImportedDirectory = directory; return Task.CompletedTask; }
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceContentEntry>>([]);
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportInstanceContentAsync(string id, string kind, string path, CancellationToken ct = default) { RootImports++; return Task.CompletedTask; }
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("fixture.zip");
    }
}
