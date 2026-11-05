using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfInstanceSelectionTests
{
    [AvaloniaFact]
    public async Task SwitchingInstanceSectionStartsAtTopOfNewPage()
    {
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        await owner.InitializeAsync(); owner.Instances.ShowInstanceSettingsCommand.Execute(null);
        var view = new InstancesView { DataContext = owner.Instances };
        var window = new Window { Width = 976, Height = 400, Content = view }; window.Show();
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Pump();
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "InstancePageScroller");
        scroll.Offset = new(0, 150); Pump(); Assert.True(scroll.Offset.Y > 0);
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "export"); Pump();
        Assert.Equal(0, scroll.Offset.Y);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FilteringOutCurrentInstanceAndClearingKeepsSavedLaunchSelection()
    {
        var service = new FakeLauncherService();
        var current = service.Snapshot.Instances.Single();
        service.Publish(service.Snapshot with { Instances = [current, current with { Id = "fabric", Name = "Fabric fixture", Loader = "fabric" }] });
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        await owner.InitializeAsync();
        var view = new InstancesView { DataContext = owner.Instances };
        var window = new Window { Width = 976, Height = 636, Content = view }; window.Show();
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Pump();
        var search = view.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "InstanceSearch");
        search.Text = "Fabric"; Pump();
        Assert.Single(owner.Instances.FilteredInstances);
        Assert.Equal(current.Id, service.Snapshot.SelectedInstanceId);
        Assert.Equal(current.Id, owner.Instances.SelectedInstance?.Id);
        search.Text = ""; Pump();
        Assert.Equal(2, owner.Instances.FilteredInstances.Count);
        Assert.Equal(current.Id, owner.Instances.SelectedInstance?.Id);
        Assert.Equal(current.Id, ((SHOU.Launcher.Core.Abstractions.GameInstance?)view.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "InstanceSelector").SelectedItem)?.Id);
        Assert.True(owner.Instances.LaunchCommand.CanExecute(null));
        window.Close();
    }
}
