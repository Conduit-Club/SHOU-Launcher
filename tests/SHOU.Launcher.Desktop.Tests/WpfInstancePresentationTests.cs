using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfInstancePresentationTests
{
    [AvaloniaFact]
    public async Task ActualControlsSavePresentationFavoriteFilterAndCancelDescriptionDraft()
    {
        var root = FixturePaths.Create("presentation");
        using var http = new HttpClient(); var downloads = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(root), downloads); using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloads, new NetworkOptions());
        await service.InitializeAsync(); var instance = await service.CreateInstanceAsync("Presentation fixture", "1.21.1");
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync();
        var vm = owner.Instances; vm.ShowInstanceSettingsCommand.Execute(null);
        var view = new InstancesView { DataContext = vm }; var window = new Window { Width = 976, Height = 636, Content = view }; window.Show();
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Pump();
        var icon = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "InstanceIconSelector");
        icon.SelectedItem = vm.InstanceIcons.Single(i => i.Id == "quilt"); Pump();
        vm.PresentationCategory = "测试"; vm.EditDescriptionCommand.Execute(null); Pump();
        var description = view.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "InstanceDescription"); description.Text = "本机测试实例"; Pump();
        Assert.True(vm.IsDescriptionEditorOpen); await vm.SavePresentationCommand.ExecuteAsync(null); Pump();
        var saved = service.Snapshot.Instances.Single().Presentation!;
        Assert.Equal("quilt", saved.Icon); Assert.Equal("测试", saved.Category); Assert.Equal("本机测试实例", saved.Description); Assert.False(vm.IsDescriptionEditorOpen);
        await vm.ToggleFavoriteCommand.ExecuteAsync(null); Pump(); Assert.True(service.Snapshot.Instances.Single().Presentation!.Favorite);
        vm.ShowSelectionCommand.Execute(null); vm.FavoritesOnly = true; Pump(); Assert.Single(vm.FilteredInstances);
        await vm.ToggleFavoriteCommand.ExecuteAsync(null); Pump(); Assert.Empty(vm.FilteredInstances); Assert.Equal(instance.Id, service.Snapshot.SelectedInstanceId);
        vm.FavoritesOnly = false; vm.ShowInstanceSettingsCommand.Execute(null); vm.EditDescriptionCommand.Execute(null); vm.PresentationDescription = "discard";
        vm.CancelDescriptionCommand.Execute(null); Assert.Equal("本机测试实例", vm.PresentationDescription);
        Assert.Equal("0 次", vm.LaunchCountLabel);
        var config = Path.Combine(instance.GameDirectory, "config"); Directory.CreateDirectory(config); await File.WriteAllTextAsync(Path.Combine(config, "fixture.json"), "{}");
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "export"); Pump();
        Assert.True(vm.ExportModrinthPackCommand.CanExecute(null));
        await vm.ExportModrinthPackCommand.ExecuteAsync(null); Pump();
        Assert.Contains(".mrpack", vm.Feedback); Assert.Single(Directory.GetFiles(Path.Combine(platform.Paths.CacheDirectory, "exports"), "*.mrpack"));
        window.Close();
    }
}
