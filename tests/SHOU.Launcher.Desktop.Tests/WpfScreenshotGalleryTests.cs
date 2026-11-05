using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfScreenshotGalleryTests
{
    [AvaloniaFact]
    public async Task GalleryReadsRealPngBoundsRejectsMalformedImageAndClearsOnNavigation()
    {
        var root = FixturePaths.Create("gallery");
        using var http = new HttpClient(); var downloads = new VerifiedDownloader(http);
        var platform = PlatformServicesFactory.Create(new(root), downloads);
        using var lease = platform.DataLock.Acquire(platform.Paths);
        await using var processes = (ProcessRunner)platform.Processes;
        await using var service = new LauncherService(platform, http, downloads, new NetworkOptions());
        await service.InitializeAsync(); var instance = await service.CreateInstanceAsync("Gallery fixture", "1.21.1");
        var screenshots = Path.Combine(instance.GameDirectory, "screenshots"); Directory.CreateDirectory(screenshots);
        await File.WriteAllBytesAsync(Path.Combine(screenshots, "fixture.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        await File.WriteAllTextAsync(Path.Combine(screenshots, "broken.png"), "fixture invalid image");
        using var owner = new MainWindowViewModel(service, new FakeExternalLauncher());
        await owner.InitializeAsync();
        owner.Instances.ShowInstanceSettingsCommand.Execute(null);
        owner.Instances.BeginRenameCommand.Execute(null);
        Assert.True(owner.Instances.IsRenameEditorOpen); Assert.True(owner.Instances.SaveRenameCommand.CanExecute(null));
        owner.Instances.RenameDraft = "Renamed gallery fixture";
        await owner.Instances.SaveRenameCommand.ExecuteAsync(null);
        Assert.False(owner.Instances.IsRenameEditorOpen);
        Assert.Equal("Renamed gallery fixture", service.Snapshot.Instances.Single().Name);
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "screenshots");
        Dispatcher.UIThread.RunJobs();
        await (owner.Instances.RefreshContentCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(2, owner.Instances.ScreenshotPreviews.Count);
        var image = owner.Instances.ScreenshotPreviews.Single(p => p.Name == "fixture.png");
        Assert.NotNull(image.Preview); Assert.InRange(image.Preview.PixelSize.Width, 1, 480); Assert.InRange(image.Preview.PixelSize.Height, 1, 270);
        Assert.False(owner.Instances.ScreenshotPreviews.Single(p => p.Name == "broken.png").HasPreview);
        var window = new Window { Width = 976, Height = 636, Content = new InstancesView { DataContext = owner.Instances } }; window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.True(window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ScreenshotGallery").IsEffectivelyVisible);
        var clipboard = window.Clipboard!;
        await owner.Instances.CopyScreenshotAsync(image, clipboard);
        using (var data = await clipboard.TryGetDataAsync()) Assert.True(data!.Contains(DataFormat.Bitmap));
        await image.RemoveCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        Assert.False(File.Exists(Path.Combine(screenshots, "fixture.png")));
        Assert.True(owner.Instances.RestoreScreenshotCommand.CanExecute(null));
        await owner.Instances.RestoreScreenshotCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
        Assert.True(File.Exists(Path.Combine(screenshots, "fixture.png")));
        Assert.False(owner.Instances.RestoreScreenshotCommand.CanExecute(null));
        await clipboard.ClearAsync();
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "overview");
        Assert.Empty(owner.Instances.ScreenshotPreviews);
        var resources = Path.Combine(instance.GameDirectory, "resourcepacks"); Directory.CreateDirectory(resources);
        await File.WriteAllTextAsync(Path.Combine(resources, "queued-fixture.zip"), "list fixture only");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = owner.RunAsync("navigation gate fixture", _ => gate.Task);
        owner.Instances.SelectedSection = owner.Instances.Sections.Single(s => s.Key == "resourcepacks");
        Dispatcher.UIThread.RunJobs(); Assert.True(owner.IsBusy);
        gate.SetResult(); await busy; Dispatcher.UIThread.RunJobs();
        await (owner.Instances.RefreshContentCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal("queued-fixture.zip", Assert.Single(owner.Instances.ContentEntries).Name);
        window.Close();
    }
}
