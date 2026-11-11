using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class PackBackgroundTaskTests
{
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 300 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } Assert.True(condition()); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task<ContentFileRowViewModel> Open(MainWindowViewModel vm)
    { vm.ShowDownloadsCommand.Execute(null); vm.Downloads.SelectCategory("modpack"); await vm.Downloads.OpenProjectAsync(PackFixture.Project); return vm.Downloads.ReleaseGroups.Single().Releases.Single().Files.Single(); }
    [AvaloniaFact]
    public async Task PackRowStartsBackgroundPipelineAndToastNavigatesToProgressAtMinimumSize()
    {
        var service = new PackFixture(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { Width = 880, Height = 560, DataContext = vm }; window.Show();
        try
        {
            var row = await Open(vm); var running = row.DownloadCommand.ExecuteAsync(null);
            await Until(() => vm.Downloads.PackTasks.Count == 1 && vm.IsTaskNotificationVisible);
            Assert.False(vm.ShowOperationStatus); Assert.Null(vm.ErrorMessage); Assert.False(running.IsCompleted);
            vm.ViewTaskNotificationCommand.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(vm.Downloads.IsTasksPage); Assert.Equal("任务管理", vm.NavigationTitle);
            var sidebar = window.GetVisualDescendants().OfType<Border>().Single(c => c.DataContext == vm.Downloads && c.IsEffectivelyVisible && c.Width == 180 && c.Padding.Top == 28);
            Assert.Equal(180, sidebar.Bounds.Width);
            Assert.True(Find<ItemsControl>(window, "PackInstallTasks").IsEffectivelyVisible);
            vm.ShowHomeCommand.Execute(null); Assert.False(vm.IsDownloadsSelected);
            vm.ShowDownloadsCommand.Execute(null); await vm.Downloads.OpenProjectAsync(PackFixture.Project);
            Assert.True(vm.Downloads.HasReleases); Assert.False(row.DownloadCommand.CanExecute(null));
            service.Download.SetResult("/fixture/pack.zip"); await running; Dispatcher.UIThread.RunJobs();
            var task = Assert.Single(vm.Downloads.PackTasks); Assert.True(task.IsCompleted); Assert.Equal(100, task.OverallProgress);
            Assert.All(task.Steps, s => Assert.Equal("已完成", s.State)); Assert.Equal(1, service.Imports); Assert.True(service.Snapshot.Instances.Last().Installed);
            await row.DownloadCommand.ExecuteAsync(null); Assert.Single(vm.Downloads.PackTasks); Assert.Equal(1, service.Imports);
            Assert.Null(service.LastLaunchServerId); Assert.Null(vm.ErrorMessage);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task CancelledDownloadCanRetryAndLateProgressCannotChangeCompletedTask()
    {
        var service = new PackFixture(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var row = await Open(vm); var pending = row.DownloadCommand.ExecuteAsync(null); await Until(() => vm.Downloads.PackTasks.Count == 1);
        var task = vm.Downloads.PackTasks.Single(); task.CancelCommand.Execute(null); await pending;
        Assert.Equal("已取消", task.State); Assert.Equal(0, service.Imports); Assert.True(task.RetryCommand.CanExecute(null));
        service.Download.SetResult("/fixture/pack.zip"); await task.RetryCommand.ExecuteAsync(null);
        Assert.True(task.IsCompleted); service.LastProgress!.Report(new("late stale data", 0, 200)); Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted); Assert.Equal(100, task.OverallProgress); Assert.Equal(1, service.Imports);
        Assert.Null(service.LastLaunchServerId);
    }
    [AvaloniaFact]
    public async Task BaseGameFailureKeepsImportedInstanceAndRetryDoesNotReimport()
    {
        var service = new PackFixture(); service.Download.SetResult("/fixture/pack.zip");
        service.InstallBehavior = _ => throw new IOException("synthetic preparation failure");
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var row = await Open(vm); await row.DownloadCommand.ExecuteAsync(null); var task = vm.Downloads.PackTasks.Single();
        Assert.Equal("失败", task.State); Assert.Equal(1, service.Imports); Assert.Contains("synthetic preparation", task.Error); Assert.Null(vm.ErrorMessage);
        service.InstallBehavior = null; await task.RetryCommand.ExecuteAsync(null);
        Assert.True(task.IsCompleted); Assert.Equal(1, service.Imports); Assert.Null(vm.ErrorMessage);
    }
    [AvaloniaFact]
    public async Task ManualFilesKeepPackPendingRejectWrongFileAndContinueAfterEveryFileIsVerified()
    {
        var service = new PackFixture { NeedManualFiles = true }; service.Download.SetResult("/fixture/pack.zip");
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var row = await Open(vm); await row.DownloadCommand.ExecuteAsync(null); var task = Assert.Single(vm.Downloads.PackTasks);
        Assert.Equal("需要补充文件", task.State); Assert.Equal(2, task.ManualFiles.Count); Assert.Equal(0, service.Imports);
        await task.ProvideManualFileAsync(task.ManualFiles[0], "/fixture/wrong.jar");
        Assert.Equal(2, task.ManualFiles.Count); Assert.Equal(0, service.Imports);
        await task.ProvideManualFileAsync(task.ManualFiles[0], "/fixture/correct.jar");
        Assert.Single(task.ManualFiles); Assert.Equal(0, service.Imports);
        await task.ProvideManualFileAsync(task.ManualFiles[0], "/fixture/correct.jar");
        Assert.True(task.IsCompleted); Assert.False(task.HasManualFiles); Assert.Equal(1, service.Imports); Assert.Null(service.LastLaunchServerId);
    }
    private sealed class PackFixture : FakeLauncherService, ILauncherCatalogService, ILocalPackService, ILocalPackProgressService, ILocalPackManualFileService
    {
        internal static ContentProject Project => new("cf-1", "fixture", "Fixture Pack", "Synthetic pack", "QA", 1, "modpack", null, new("https://www.curseforge.com/minecraft/modpacks/fixture"), Source: "curseforge");
        internal TaskCompletionSource<string> Download { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Imports;
        internal bool NeedManualFiles;
        private readonly HashSet<int> provided = [];
        internal IProgress<PackInstallProgress>? LastProgress;
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken ct = default) => Task.FromResult(new GameCatalog([], "fixture"));
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoaderCatalogVersion>>([]);
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentProject>>([Project]);
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentRelease>>([new("1", "Fixture 1", "1.0.0", DateTimeOffset.UtcNow, ["1.21.1"], ["fabric"], [new("fixture.zip", new("https://edge.forgecdn.net/fixture.zip"), new('a', 40), 100)])]);
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken ct = default) => Download.Task.WaitAsync(ct);
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<LocalPackInspection> InspectLocalPackAsync(string path, CancellationToken ct = default) => Task.FromResult(new LocalPackInspection(LocalPackFormat.CurseForge, "Fixture Pack", "1.21.1", "fabric", "0.16.14", new('b', 64), [], !NeedManualFiles || provided.Count == 2,
            ManualFiles: NeedManualFiles ? new[] { 1, 2 }.Where(id => !provided.Contains(id)).Select(id => new CurseForgeManualFile(1, id, id + ".jar", new('a', 40), 10, new("https://www.curseforge.com/projects/1/files/" + id))).ToArray() : []));
        public Task ProvideLocalPackFileAsync(string archivePath, string expectedArchiveSha256, int projectId, int fileId, string sourcePath, CancellationToken cancellationToken = default)
        { if (sourcePath.EndsWith("wrong.jar", StringComparison.Ordinal)) throw new InvalidDataException("Wrong hash"); provided.Add(fileId); return Task.CompletedTask; }
        public Task<LocalPackInspection> InspectLocalPackWithProgressAsync(string path, IProgress<PackInstallProgress> progress, CancellationToken ct = default) => InspectLocalPackAsync(path, ct);
        public Task<LocalPackImportResult> ImportLocalPackWithProgressAsync(LocalPackImportRequest request, IProgress<PackInstallProgress> progress, CancellationToken ct = default)
        { LastProgress = progress; progress.Report(new("安装整合包文件", 1, 1)); return ImportLocalPackAsync(request, ct); }
        public Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Imports++;
            var instance = new GameInstance("new-pack", request.InstanceName, "1.21.1", Path.Combine(Paths.InstancesDirectory, "new-pack"));
            Publish(Snapshot with { Instances = [.. Snapshot.Instances, instance], SelectedInstanceId = instance.Id });
            return Task.FromResult(new LocalPackImportResult(instance, 1, []));
        }
    }
}
