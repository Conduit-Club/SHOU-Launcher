using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.Tools;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class InstanceManagementUiTests
{
    [AvaloniaFact]
    public async Task LegacyHiddenCategoryRequiresExplicitFilterAndDoesNotLoseSavedSelection()
    {
        var service = new FakeLauncherService(); var visible = service.Snapshot.Instances.Single(); var hidden = visible with { Id = "hidden", Name = "Hidden legacy", Presentation = new("auto", "隐藏") };
        service.Publish(service.Snapshot with { Instances = [visible, hidden] }); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync();
        Assert.Equal(visible.Id, owner.Instances.FilteredInstances.Single().Id); owner.Instances.InstanceCategoryFilter = "隐藏"; Assert.Equal(hidden.Id, owner.Instances.FilteredInstances.Single().Id); Assert.Equal(visible.Id, service.Snapshot.SelectedInstanceId);
    }
    [AvaloniaFact]
    public async Task LongInstanceNameStaysOnOneLineInListAndHomeAndUsesRealArtworkControls()
    {
        var service = new FakeLauncherService(); var first = service.Snapshot.Instances.Single() with { Name = "XPlus PerioTable based on Minecraft 26.2 (Fabric) " + new string('长', 90) };
        service.Publish(service.Snapshot with { Instances = [first] }); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync();
        var window = new MainWindow { Width = 880, Height = 560, DataContext = owner }; window.Show();
        try
        {
            owner.SelectedPage = owner.Instances; Pump(window);
            var label = Find<TextBlock>(window, "InstanceRowName"); Assert.Equal(TextWrapping.NoWrap, label.TextWrapping); Assert.Equal(1, label.MaxLines); Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming); Assert.True(label.Bounds.Height <= 24); Assert.True(label.Bounds.Width < 700);
            Assert.Contains(window.GetVisualDescendants().OfType<InstanceArtwork>(), c => c.Instance?.Id == first.Id);
            owner.SelectedPage = owner.Home; Pump(window); var home = Find<TextBlock>(window, "HomeSelectedInstanceName"); Assert.Equal(first.Name, home.Text); Assert.Equal(TextWrapping.NoWrap, home.TextWrapping); Assert.Equal(1, home.MaxLines); Assert.True(home.Bounds.Height <= 20);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task FolderCategoryAndFavoritesFiltersDoNotOverwriteSavedLaunchSelection()
    {
        var service = new FakeLauncherService(); var first = service.Snapshot.Instances.Single(); var path = Path.Combine(Path.GetTempPath(), "r20-ui-folder");
        var external = first with { Id = "external", Name = "External Fabric", Loader = "fabric", Origin = new(path, Path.Combine(path, "versions/ext"), "ext", new string('a', 64)), Presentation = new("fabric", "整合包", "", true) };
        service.Publish(service.Snapshot with { Instances = [first, external], SelectedInstanceId = first.Id }); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync();
        var vm = owner.Instances; vm.SelectedGameFolder = vm.GameFolders.Single(f => f.Path == path); Assert.Single(vm.FilteredInstances); Assert.Equal(first.Id, service.Snapshot.SelectedInstanceId);
        vm.InstanceCategoryFilter = "整合包"; vm.FavoritesOnly = true; Assert.Equal(external.Id, vm.FilteredInstances.Single().Id);
        vm.SearchText = "no result"; Assert.Empty(vm.FilteredInstances); Assert.Equal(first.Id, service.Snapshot.SelectedInstanceId);
        vm.SearchText = ""; vm.FavoritesOnly = false; vm.InstanceCategoryFilter = "全部分类"; vm.SelectedGameFolder = vm.GameFolders.Single(f => f.Path is null); Assert.Equal(2, vm.FilteredInstances.Count); Assert.Equal(first.Id, vm.SelectedInstance?.Id);
    }
    [AvaloniaFact]
    public async Task FolderPreviewNeedsExplicitBatchConfirmationAndReportsSkippedProfiles()
    {
        var service = new BatchService(); using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync(); var vm = owner.Instances;
        var window = new Window { Width = 976, Height = 636, Content = new InstancesView { DataContext = vm } }; window.Show();
        try
        {
            await vm.InspectExistingFolderAsync(service.Folder); Pump(window); Assert.Equal(0, service.Attached); Assert.Equal(2, vm.ExistingCandidates.Count); Assert.Single(vm.ExistingIssues);
            Assert.True(Find<Button>(window, "ConfirmAllExistingInstancesButton").IsEffectivelyEnabled);
            await vm.AttachAllExistingCommand.ExecuteAsync(null); Pump(window); Assert.Equal(2, service.Attached); Assert.False(vm.HasExistingCandidates); Assert.Equal(service.Folder, vm.SelectedGameFolder?.Path); Assert.Equal(2, vm.FilteredInstances.Count);
            await vm.InspectExistingFolderAsync(service.Folder); Assert.False(vm.HasExistingCandidates); Assert.Contains("已接入", vm.Feedback); Assert.Equal(2, service.Attached);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task FailedBatchPreservesPreviewAndDismissDoesNotRegisterAnything()
    {
        var service = new BatchService { Fail = true }; using var owner = new MainWindowViewModel(service, new FakeExternalLauncher()); await owner.InitializeAsync(); var vm = owner.Instances;
        await vm.InspectExistingFolderAsync(service.Folder); await vm.AttachAllExistingCommand.ExecuteAsync(null); Assert.Equal(0, service.Attached); Assert.True(vm.HasExistingCandidates); Assert.False(vm.IsFolderBusy);
        vm.DismissExistingCommand.Execute(null); Assert.False(vm.HasExistingCandidates); Assert.Single(service.Snapshot.Instances);
    }
    [AvaloniaFact]
    public void CropCancelWritesNothingAndZoomPositionAndRotationProduceSquareImage()
    {
        var pixels = Pixels(); var calls = 0; var window = new InstanceIconCropWindow(pixels, _ => { calls++; return Task.FromResult(true); }); window.Show();
        window.Model.Zoom = 2; window.Model.OffsetX = 1; window.Model.Rotate(); var image = AccountAppearancePng.DecodeIcon(window.Model.Crop()); Assert.Equal(256, image.Width); Assert.Equal(image.Width, image.Height);
        Click(Find<Button>(window, "CancelInstanceIconCrop")); Assert.Equal(0, calls); Assert.False(window.IsVisible);
    }
    [AvaloniaFact]
    public async Task CropSaveFailureKeepsDialogOpenAndCancellationRemainsAvailableDuringSave()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = 0;
        var window = new InstanceIconCropWindow(Pixels(), _ => gate.Task, () => { cancelled++; gate.TrySetResult(false); }); window.Show();
        try
        {
            Click(Find<Button>(window, "SaveInstanceIconCrop")); Assert.False(Find<Button>(window, "SaveInstanceIconCrop").IsEnabled); Assert.True(Find<Button>(window, "CancelInstanceIconCrop").IsEnabled);
            Click(Find<Button>(window, "CancelInstanceIconCrop")); await Task.Yield(); Pump(window); Assert.Equal(1, cancelled); Assert.True(window.IsVisible); Assert.True(Find<TextBlock>(window, "InstanceIconCropError").IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void ImageImportIsReadOnlyAndRejectsNonImageInput()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data/r20-ui", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var path = Path.Combine(root, "中文 图标.png");
        var source = ToolImages.EncodeRgba(8, 4, Pixels().Rgba.Span); File.WriteAllBytes(path, source); var decoded = InstanceIconCropModel.ReadImage(path); Assert.Equal(8, decoded.Width); Assert.Equal(4, decoded.Height); Assert.Equal(source, File.ReadAllBytes(path));
        var invalid = Path.Combine(root, "broken.png"); File.WriteAllText(invalid, "not an image"); Assert.Throws<InvalidDataException>(() => InstanceIconCropModel.ReadImage(invalid));
    }
    private static InstanceIconPixels Pixels() { var rgba = new byte[8 * 4 * 4]; for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = (byte)(i % 255); rgba[i + 2] = 200; rgba[i + 3] = 255; } return new(8, 4, rgba); }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private sealed class BatchService : FakeLauncherService, IExistingInstanceService, IExistingInstanceBatchService
    {
        public string Folder = Path.Combine(Path.GetTempPath(), "r20-public-legacy-folder"); public int Attached; public bool Fail;
        private IReadOnlyList<ExistingInstanceCandidate> Candidates => Enumerable.Range(1, 2).Select(i => new ExistingInstanceCandidate("Public " + i, "local-" + i, "1.21.1", "fabric", "0.19.3", Path.Combine(Folder, "versions/" + i), new(Folder, Path.Combine(Folder, "versions/" + i), i.ToString(), new string('a', 64)))).ToArray();
        public Task<ExistingInstanceInspection> InspectInstanceFolderDetailedAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult(new ExistingInstanceInspection(Candidates, [new("broken", "controlled invalid metadata")]));
        public Task<IReadOnlyList<ExistingInstanceCandidate>> InspectInstanceFolderAsync(string folder, CancellationToken cancellationToken = default) => Task.FromResult(Candidates);
        public async Task<GameInstance> AttachExistingInstanceAsync(ExistingInstanceCandidate candidate, CancellationToken cancellationToken = default) => (await AttachExistingInstancesAsync([candidate], cancellationToken)).Single();
        public Task<IReadOnlyList<GameInstance>> AttachExistingInstancesAsync(IReadOnlyList<ExistingInstanceCandidate> candidates, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("controlled failed save"); var items = candidates.Select(c => new GameInstance(c.VersionId, c.Name, c.VersionId, c.GameDirectory, Loader: c.Loader, GameVersionId: c.GameVersion, Origin: c.Origin)).ToArray(); Attached += items.Length;
            Publish(Snapshot with { Instances = Snapshot.Instances.Concat(items).ToArray(), SelectedInstanceId = items[0].Id }); return Task.FromResult<IReadOnlyList<GameInstance>>(items);
        }
    }
}
