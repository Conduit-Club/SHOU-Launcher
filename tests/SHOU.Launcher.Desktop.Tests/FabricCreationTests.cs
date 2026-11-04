using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class FabricCreationTests
{
    private static MainWindow Open(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window); vm.SelectedPage = vm.Instances; Pump(window);
        return window;
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task LoaderSelectionRoutesCreationToTheMatchingServiceContract()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            var selector = Find<ComboBox>(window, "NewInstanceLoaderSelector");
            Assert.False(Find<StackPanel>(window, "FabricLoaderFields").IsVisible);
            selector.SelectedItem = vm.Instances.LoaderChoices.Single(l => l.Id == "fabric"); Pump(window);
            Assert.True(Find<StackPanel>(window, "FabricLoaderFields").IsVisible);
            Assert.Equal("0.16.14", Find<TextBox>(window, "FabricLoaderVersion").Text);
            Find<Button>(window, "CreateInstanceButton").Command!.Execute(null);
            await vm.Instances.CreateInstanceCommand.ExecutionTask!; Pump(window);
            Assert.Equal(1, fake.FabricCreateCalls); Assert.Equal(0, fake.VanillaCreateCalls);
            Assert.Equal("1.21.1", fake.LastFabricRequest!.Value.GameVersion);
            Assert.Equal("0.16.14", fake.LastFabricRequest.Value.LoaderVersion);
            var fabric = fake.Snapshot.Instances.Single(i => i.Loader == "fabric");
            Assert.Equal("0.16.14", fabric.LoaderVersion);
            Assert.Contains("Fabric Loader 0.16.14", vm.Instances.InstanceSummary);
            selector.SelectedItem = vm.Instances.LoaderChoices.Single(l => l.Id == "vanilla"); Pump(window);
            await vm.Instances.CreateInstanceCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal(1, fake.FabricCreateCalls); Assert.Equal(1, fake.VanillaCreateCalls);
            Assert.Equal("vanilla", vm.Instances.SelectedInstance!.Loader);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FabricCancellationAndErrorsStayExplicitAndUnknownLoadersNeverUseVanilla()
    {
        var fake = new FakeLauncherService { FabricCreateBehavior = ct => Task.Delay(Timeout.Infinite, ct) };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.Instances.SelectedLoader = vm.Instances.LoaderChoices.Single(l => l.Id == "fabric");
            vm.Instances.CreateInstanceCommand.Execute(null); Pump(window);
            Assert.True(vm.IsBusy); Assert.False(Find<ComboBox>(window, "NewInstanceLoaderSelector").IsEffectivelyEnabled);
            vm.CancelOperationCommand.Execute(null);
            await vm.Instances.CreateInstanceCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5)); Pump(window);
            Assert.True(fake.FabricCreationCancelled); Assert.Single(fake.Snapshot.Instances);
            Assert.Contains("取消", vm.StateSummary);
            fake.FabricCreateBehavior = _ => throw new InvalidDataException("Fabric 元数据校验失败");
            await vm.Instances.CreateInstanceCommand.ExecuteAsync(null); Pump(window);
            Assert.True(vm.HasError); Assert.Contains("Fabric 元数据校验失败", vm.ErrorMessage);
            Assert.Single(fake.Snapshot.Instances); Assert.Equal(0, fake.VanillaCreateCalls);
            vm.Instances.SelectedLoader = new("quilt", "Quilt");
            Assert.False(vm.Instances.CreateInstanceCommand.CanExecute(null));
            await vm.Instances.CreateInstanceCommand.ExecuteAsync(null);
            Assert.Equal(0, fake.VanillaCreateCalls);
            var unknown = new GameInstance("unsupported", "未知加载器", "1.21.1", "/fixture/unsupported", Loader: "quilt", Installed: true);
            fake.Publish(fake.Snapshot with { Instances = [unknown], SelectedInstanceId = unknown.Id }); Pump(window);
            Assert.Contains("quilt", vm.Instances.InstanceSummary); Assert.Contains("不支持", vm.Instances.LaunchPrerequisite);
            Assert.False(vm.Instances.InstallCommand.CanExecute(null)); Assert.False(vm.Instances.LaunchCommand.CanExecute(null));
            Assert.False(vm.Instances.InspectPlanCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }
}
