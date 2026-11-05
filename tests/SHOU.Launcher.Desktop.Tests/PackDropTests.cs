using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class PackDropTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static async Task<DataTransfer> Files(Window window, params string[] paths)
    {
        var data = new DataTransfer();
        var root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT")!, "test-data", "r6", "drop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var name in paths)
        {
            var path = System.IO.Path.Combine(root, System.IO.Path.GetFileName(name));
            await File.WriteAllTextAsync(path, "fixture file for routed drag-drop tests");
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
            data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageFile>(file)));
        }
        return data;
    }
    private static DragEventArgs Event(RoutedEvent<DragEventArgs> routed, IDataTransfer transfer, MainWindow window)
        => new(routed, transfer, window, new Point(450, 260), KeyModifiers.None) { DragEffects = DragDropEffects.Copy };

    [AvaloniaTheory]
    [InlineData("/fixture/中文 pack.mrpack", LocalPackFormat.Modrinth)]
    [InlineData("/fixture/中文 pack.zip", LocalPackFormat.CurseForge)]
    public async Task FileDropOpensInspectionAndWaitsForExplicitInstall(string path, LocalPackFormat format)
    {
        var fixture = new PackCatalog { Format = format };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher(), fixture);
        var window = new MainWindow { DataContext = owner }; window.Show(); Pump(window);
        try
        {
            using var data = await Files(window, path);
            var over = Event(DragDrop.DragOverEvent, data, window); window.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            window.RaiseEvent(Event(DragDrop.DropEvent, data, window)); Pump(window);
            Assert.Same(owner.Downloads, owner.SelectedPage);
            Assert.Equal("modpack", owner.Downloads.SelectedCategory.Id);
            Assert.Equal(System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(fixture.Inspected));
            Assert.Equal(format, owner.Downloads.DetectedLocalPack!.Format);
            Assert.Null(fixture.Imported);
            owner.Downloads.PackInstanceName = "Explicit choice";
            if (format == LocalPackFormat.Modrinth)
            {
                var optional = Assert.Single(owner.Downloads.OptionalPackFiles);
                Assert.False(optional.Selected); optional.Selected = true;
            }
            Assert.True(owner.Downloads.ImportLocalPackCommand.CanExecute(null));
            await owner.Downloads.ImportLocalPackCommand.ExecuteAsync(null);
            Assert.Equal("Explicit choice", fixture.Imported!.InstanceName);
            Assert.Equal(new string('c', 64), fixture.Imported.ExpectedSha256);
            Assert.Equal(format == LocalPackFormat.Modrinth ? new[] { "mods/optional.jar" } : [], fixture.Imported.OptionalClientFiles);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MultipleFilesTextAndBusyWindowsDoNotQueueAnInstall()
    {
        var fixture = new PackCatalog();
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher(), fixture);
        var window = new MainWindow { DataContext = owner }; window.Show(); Pump(window);
        try
        {
            foreach (var paths in new[] { new[] { "/fixture/not-a-pack.txt" }, new[] { "/fixture/a.zip", "/fixture/b.mrpack" } })
            {
                using var data = await Files(window, paths);
                var over = Event(DragDrop.DragOverEvent, data, window); window.RaiseEvent(over);
                Assert.Equal(DragDropEffects.None, over.DragEffects);
                window.RaiseEvent(Event(DragDrop.DropEvent, data, window)); Pump(window);
            }
            using var text = new DataTransfer(); text.Add(DataTransferItem.CreateText("https://example.invalid/pack.zip"));
            var textOver = Event(DragDrop.DragOverEvent, text, window); window.RaiseEvent(textOver);
            Assert.Equal(DragDropEffects.None, textOver.DragEffects);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = owner.RunAsync("test busy operation", _ => release.Task);
            using var pack = await Files(window, "/fixture/a.zip");
            var busyOver = Event(DragDrop.DragOverEvent, pack, window); window.RaiseEvent(busyOver);
            Assert.Equal(DragDropEffects.None, busyOver.DragEffects);
            window.RaiseEvent(Event(DragDrop.DropEvent, pack, window));
            release.SetResult(); await operation; Pump(window);
            Assert.Null(fixture.Inspected); Assert.Null(fixture.Imported);
            Assert.Same(owner.Home, owner.SelectedPage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RecognizedUnsupportedPackShowsItsReasonAndCannotBeImported()
    {
        var fixture = new PackCatalog { Supported = false, Format = LocalPackFormat.CurseForge, Warnings = ["项目 7 / 文件 11：作者未提供下载，尚未完成。"] };
        using var owner = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher(), fixture);
        var window = new MainWindow { DataContext = owner }; window.Show(); Pump(window);
        try
        {
            using var data = await Files(window, "/fixture/blocked.zip");
            window.RaiseEvent(Event(DragDrop.DropEvent, data, window)); Pump(window);
            Assert.Same(owner.Downloads, owner.SelectedPage);
            Assert.Contains("未提供下载", owner.Downloads.PackStatus);
            Assert.False(owner.Downloads.ImportLocalPackCommand.CanExecute(null));
            var details = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "LocalPackDetails" && e.IsEffectivelyVisible);
            details.IsExpanded = true; Pump(window);
            Assert.Contains("项目 7 / 文件 11", Assert.IsType<TextBlock>(details.Content).Text);
            Assert.Null(fixture.Imported);
        }
        finally { window.Close(); }
    }

    private sealed class PackCatalog : ILauncherCatalogService, ILocalPackService
    {
        public LocalPackFormat Format = LocalPackFormat.Modrinth;
        public bool Supported = true;
        public string[] Warnings = [];
        public string? Inspected;
        public LocalPackImportRequest? Imported;
        public Task<LocalPackInspection> InspectLocalPackAsync(string archivePath, CancellationToken cancellationToken = default)
        {
            Inspected = archivePath;
            var mr = Format == LocalPackFormat.Modrinth ? new ModrinthPackInspection("Fixture", "1", "1.21.1", "fabric", "0.16.14", new string('c',64),
                [new("mods/optional.jar", new string('a',40), new string('b',128), 1, [], "optional")], [], [], Supported) : null;
            return Task.FromResult(new LocalPackInspection(Format,"Fixture", "1.21.1", "fabric", "0.16.14",new string('c',64),Warnings,Supported,
                Supported ? null : "此整合包的部分文件未提供下载。", mr));
        }
        public Task<LocalPackImportResult> ImportLocalPackAsync(LocalPackImportRequest request, CancellationToken cancellationToken = default)
        { Imported = request; return Task.FromResult(new LocalPackImportResult(new("fixture",request.InstanceName,"1.21.1","/fixture/instance"),1,[])); }
        public Task<GameCatalog> GetGameCatalogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LoaderCatalogVersion>> GetFabricCatalogAsync(string gameVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentProject>> SearchContentAsync(ContentQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ContentRelease>> GetContentReleasesAsync(string projectId, string? gameVersion = null, string? loader = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> DownloadContentAsync(ContentFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetContentIconAsync(string iconUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

}
