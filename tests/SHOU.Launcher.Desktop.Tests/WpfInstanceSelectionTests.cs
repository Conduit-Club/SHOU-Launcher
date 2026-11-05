using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfInstanceSelectionTests
{
    [AvaloniaFact]
    public async Task MainWindowInstanceButtonOpensVisibleSelectionAfterChangingSixInstanceList()
    {
        var service = new FakeLauncherService(); var first = service.Snapshot.Instances.Single() with
        { Presentation = new("auto", "测试", "R5 saved metadata", true) };
        var rows = Enumerable.Range(0, 6).Select(i => i == 0 ? first : first with { Id = "fixture-" + i, Name = "Fixture " + i }).ToArray();
        service.Publish(service.Snapshot with { Instances = rows, SelectedInstanceId = rows[^1].Id });
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync();
        var window = new MainWindow { DataContext = owner }; window.Show(); owner.SelectedPage = owner.Instances;
        void Pump() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        Pump();
        var selector = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "InstanceSelector");
        selector.SelectedItem = owner.Instances.Instances.First(i => i.Id == first.Id); Pump();
        var button = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "OpenInstanceSettingsButton");
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Command!.CanExecute(null));
        button.Command.Execute(null); Pump(); Assert.False(owner.Instances.IsSelection);
        owner.Instances.ShowSelectionCommand.Execute(null); Pump();
        var center = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left); Pump();
        Assert.False(owner.Instances.IsSelection); Assert.Equal(first.Id, owner.Instances.SelectedInstance?.Id);
        owner.Instances.ShowSelectionCommand.Execute(null); owner.Instances.SearchText = "no matching fixture"; Pump();
        Assert.False(button.IsEffectivelyEnabled); window.Close();
    }
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
        search.Text = "no matching fixture"; Pump();
        var open = view.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "OpenInstanceSettingsButton");
        Assert.False(open.IsEffectivelyEnabled);
        Assert.False(owner.Instances.OpenVisibleInstanceCommand.CanExecute(null));
        Assert.True(owner.Instances.ShowInstanceSettingsCommand.CanExecute(null));
        Assert.Equal(current.Id, owner.Instances.SelectedInstance?.Id);
        window.Close();
    }
}
