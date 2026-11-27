using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class InstanceComponentUiTests
{
    private sealed class Service : FakeLauncherService, ILoaderInstanceService, IInstanceComponentService
    {
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CatalogReads, Changes;
        public Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken cancellationToken = default)
        { CatalogReads++; return Task.FromResult<IReadOnlyList<LoaderVersionChoice>>([new(loader, "0.16.14", true)]); }
        public Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<GameInstance> ChangeInstanceLoaderAsync(string instanceId, string loader, string? loaderVersion, CancellationToken cancellationToken = default)
        {
            Changes++; await Completion.Task.WaitAsync(cancellationToken);
            var changed = Snapshot.Instances.Single(i => i.Id == instanceId) with { Loader = loader, LoaderVersion = loaderVersion };
            Publish(Snapshot with { Instances = [changed] }); return changed;
        }
    }
    [AvaloniaFact]
    public async Task ComponentChangesRequireConfirmationAndUseLocalBusyState()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        try
        {
            root.Instances.ShowInstanceSettingsCommand.Execute(null);
            root.Instances.SelectedSection = root.Instances.Sections.Single(s => s.Key == "modify"); Pump();
            var vm = root.Instances;
            vm.ComponentLoader = vm.ComponentLoaderChoices.Single(c => c.Id == "fabric"); Pump();
            Assert.True(service.CatalogReads > 0); Assert.Single(vm.ComponentVersions);
            Assert.False(vm.ChangeInstanceComponentCommand.CanExecute(null));
            vm.ConfirmComponentChange = true; Assert.True(vm.ChangeInstanceComponentCommand.CanExecute(null));
            var change = vm.ChangeInstanceComponentCommand.ExecuteAsync(null); Pump();
            Assert.True(vm.IsComponentBusy); Assert.False(root.ShowOperationStatus);
            Assert.False(window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ComponentLoaderChoice").IsEnabled);
            service.Completion.SetResult(); await change; Pump();
            Assert.Equal(1, service.Changes); Assert.False(vm.IsComponentBusy); Assert.False(vm.ConfirmComponentChange);
            Assert.Equal("fabric", root.Snapshot.Instances.Single().Loader);
        }
        finally { window.Close(); }
    }
}
