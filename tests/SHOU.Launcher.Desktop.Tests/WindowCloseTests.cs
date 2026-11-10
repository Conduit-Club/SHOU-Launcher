using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WindowCloseTests
{
    private static void Click(MainWindow window,string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task PumpUntil(Func<bool> condition)
    { for(var i=0;i<200 && !condition();i++){ Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    [AvaloniaFact]
    public async Task ClosingActuallyWaitsUntilCancelledWriteHasFinishedAndRepeatedCloseDoesNotDisposeEarly()
    {
        var service=new FakeLauncherService(); var vm=new MainWindowViewModel(service,new FakeExternalLauncher()); var window=new MainWindow { DataContext=vm }; window.Show(); await vm.InitializeAsync();
        var cancelSeen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work=vm.RunInlineAsync("受控写盘",async ct=>{ using var registration=ct.Register(()=>cancelSeen.TrySetResult()); await cleanup.Task; });
        window.Close(); window.Close(); await PumpUntil(()=>cancelSeen.Task.IsCompleted); Assert.True(window.IsVisible); Assert.True(service.SnapshotSubscribers>0);
        cleanup.SetResult(); await work; await PumpUntil(()=>!window.IsVisible); Assert.Equal(0,service.SnapshotSubscribers); Assert.Equal(0,service.StopCalls);
    }
    [AvaloniaFact]
    public async Task UnsavedSettingsCanReturnThenSaveAndReopenWithPersistedValue()
    {
        var service=new FakeLauncherService(); var vm=new MainWindowViewModel(service,new FakeExternalLauncher()); var window=new MainWindow { DataContext=vm }; window.Show(); await vm.InitializeAsync();
        vm.Settings.MemoryMiB=4096; window.Close(); Assert.True(window.FindControl<Border>("WindowCloseOverlay")!.IsVisible);
        Click(window,"CancelWindowCloseButton"); await PumpUntil(()=>!window.FindControl<Border>("WindowCloseOverlay")!.IsVisible); Assert.True(window.IsVisible); Assert.True(vm.Settings.IsDirty);
        window.Close(); Click(window,"SaveSettingsAndCloseButton"); await PumpUntil(()=>!window.IsVisible);
        Assert.Equal(4096,service.Snapshot.Settings.MemoryMiB); using var restarted=new MainWindowViewModel(service,new FakeExternalLauncher()); Assert.Equal(4096,restarted.Settings.MemoryMiB);
    }
    [AvaloniaFact]
    public async Task InstanceDraftCannotBeSilentlySavedByTheGlobalSettingsCloseButton()
    {
        var service=new FakeLauncherService(); var vm=new MainWindowViewModel(service,new FakeExternalLauncher()); var window=new MainWindow { DataContext=vm }; window.Show(); await vm.InitializeAsync();
        vm.Instances.MemoryMiB=4096; vm.Settings.MemoryMiB=3072; window.Close();
        Assert.True(window.FindControl<Border>("WindowCloseOverlay")!.IsVisible); Assert.False(window.FindControl<Button>("SaveSettingsAndCloseButton")!.IsVisible);
        Click(window,"CancelWindowCloseButton"); await PumpUntil(()=>!window.FindControl<Border>("WindowCloseOverlay")!.IsVisible); Assert.Equal(4096,vm.Instances.MemoryMiB);
        window.Close(); Click(window,"DiscardAndCloseButton"); await PumpUntil(()=>!window.IsVisible); Assert.Equal(0,service.SaveCalls);
    }
    [AvaloniaFact]
    public async Task DiscardCloseKeepsSavedValueAndInvalidSaveKeepsWindowAndDraft()
    {
        var service=new FakeLauncherService(); var saved=service.Snapshot.Settings.MemoryMiB; var vm=new MainWindowViewModel(service,new FakeExternalLauncher()); var window=new MainWindow { DataContext=vm }; window.Show(); await vm.InitializeAsync();
        vm.Settings.GameRoot="relative-folder"; window.Close(); Click(window,"SaveSettingsAndCloseButton"); await PumpUntil(()=>!window.FindControl<Border>("WindowCloseOverlay")!.IsVisible);
        Assert.True(window.IsVisible); Assert.True(vm.Settings.IsDirty); window.Close(); Click(window,"DiscardAndCloseButton"); await PumpUntil(()=>!window.IsVisible); Assert.Equal(saved,service.Snapshot.Settings.MemoryMiB); Assert.NotEqual("relative-folder",service.Snapshot.Settings.GameRoot);
    }
}
