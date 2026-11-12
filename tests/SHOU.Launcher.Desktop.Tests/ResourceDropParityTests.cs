using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ResourceDropParityTests
{
    [AvaloniaTheory]
    [InlineData("resourcepacks", "pack.zip")]
    [InlineData("shaders", "shader.zip")]
    [InlineData("mods", "one.jar")]
    [InlineData("schematics", "one.litematic")]
    public async Task DropsInstallIntoTheCurrentResourcePageWithoutOpeningPackImport(string kind, string filename)
    {
        var service = new Service(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var vm = owner.Instances;
        vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == kind);
        var window = new MainWindow { DataContext = owner }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data", "r39-drop", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var path = Path.Combine(root, filename); await File.WriteAllTextAsync(path, "read-only source fixture"); using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile((await window.StorageProvider.TryGetFileFromPathAsync(path))!));
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, window, new Point(450, 260), KeyModifiers.None) { DragEffects = DragDropEffects.Copy }; window.RaiseEvent(over); Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, new Point(450, 260), KeyModifiers.None) { DragEffects = DragDropEffects.Copy });
            for (var n = 0; n < 60 && service.Imported.Count == 0; n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Assert.Equal((kind, path), Assert.Single(service.Imported)); Assert.Same(vm, owner.SelectedPage); Assert.Equal("read-only source fixture", await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task MultipleModsAreInstalledAndTargetChangesBeforeImportAreRejected()
    {
        var service = new Service(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); var vm = owner.Instances; vm.ShowInstanceSettingsCommand.Execute(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "mods");
        var expected = vm.SelectedInstance!; await vm.ImportDroppedContentAsync(expected, "mods", ["/fixture/one.jar", "/fixture/two.jar"]); Assert.Equal(2, service.Imported.Count);
        await vm.ImportDroppedContentAsync(expected with { Id = "other" }, "mods", ["/fixture/wrong.jar"]); Assert.Equal(2, service.Imported.Count);
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService
    {
        public List<(string Kind, string Path)> Imported = [];
        public Service() { var i = Snapshot.Instances.Single() with { Loader = "fabric", GameVersionId = "1.21.1" }; Publish(Snapshot with { Instances = [i] }); }
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstanceContentEntry>>([]);
        public Task ImportInstanceContentAsync(string id, string kind, string source, CancellationToken ct = default) { Imported.Add((kind, source)); return Task.CompletedTask; }
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("/fixture/out.zip");
    }
}
