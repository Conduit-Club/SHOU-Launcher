using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BitMiracle.LibTiff.Classic;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SkiaSharp;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ScreenshotFormatAndPagingTests
{
    private static async Task Wait(Func<bool> condition)
    { for (var n = 0; n < 300 && !condition(); n++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } Assert.True(condition()); }
    [AvaloniaTheory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("tif")]
    [InlineData("tiff")]
    public async Task EveryListedImageFormatCanPreviewCopyAndOpenItsExactFile(string extension)
    {
        var service = new Service(); var path = Path.Combine(FixturePaths.Create("screenshot-formats"), "sample." + extension); WriteImage(path, extension); service.Files = [Entry(path)]; var external = new External();
        using var root = Open(service, external); var vm = root.Instances; await Wait(() => vm.ScreenshotPreviews.Count == 1 && !root.IsBusy);
        var row = vm.ScreenshotPreviews.Single(); Assert.True(row.HasPreview); Assert.InRange(row.Preview!.PixelSize.Width, 1, 480); Assert.InRange(row.Preview.PixelSize.Height, 1, 270);
        var w = new Window(); w.Show();
        try { await vm.CopyScreenshotAsync(row, w.Clipboard!); using var data = await w.Clipboard!.TryGetDataAsync(); Assert.True(data!.Contains(DataFormat.Bitmap)); await w.Clipboard.ClearAsync(); }
        finally { w.Close(); }
        await row.OpenCommand.ExecuteAsync(null); Assert.Equal(path, external.Opened.Single());
        vm.SelectedSection = vm.Sections.Single(s => s.Key == "overview"); await row.OpenCommand.ExecuteAsync(null); Assert.Single(external.Opened);
    }
    [AvaloniaFact]
    public async Task PagingReachesEveryFileBeyondSixtyAndOldLoadCannotPopulateAnotherPage()
    {
        var service = new Service(); var folder = FixturePaths.Create("screenshot-pages");
        service.Files = Enumerable.Range(0, 83).Select(n => { var path = Path.Combine(folder, n.ToString("D3") + ".png"); WriteImage(path, "png"); return Entry(path); }).ToArray();
        using var root = Open(service, new External()); var vm = root.Instances; await Wait(() => vm.ScreenshotPreviews.Count == 20 && !root.IsBusy);
        Assert.True(vm.HasMoreScreenshots);
        while (vm.HasMoreScreenshots) await vm.LoadMoreScreenshotsCommand.ExecuteAsync(null);
        Assert.Equal(83, vm.ScreenshotPreviews.Count); Assert.Equal(83, vm.ScreenshotPreviews.Select(p => p.RelativePath).Distinct().Count()); Assert.False(vm.LoadMoreScreenshotsCommand.CanExecute(null));
        Assert.Contains("83/83", vm.ScreenshotSummary);
        await vm.RefreshContentCommand.ExecuteAsync(null); Assert.Equal(20, vm.ScreenshotPreviews.Count);
        var more = vm.LoadMoreScreenshotsCommand.ExecuteAsync(null); vm.SelectedSection = vm.Sections.Single(s => s.Key == "resourcepacks"); await more; Assert.Empty(vm.ScreenshotPreviews);
    }
    private static MainWindowViewModel Open(Service service, IExternalLauncher external)
    { var root = new MainWindowViewModel(service, external); root.Instances.ShowInstanceSettingsCommand.Execute(null); root.Instances.SelectedSection = root.Instances.Sections.Single(s => s.Key == "screenshots"); return root; }
    private static InstanceContentEntry Entry(string path) => new(Path.GetFileName(path), "screenshots/" + Path.GetFileName(path), path, "screenshots", new FileInfo(path).Length, DateTimeOffset.UtcNow);
    internal static void WriteImage(string path, string extension)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (extension is "tif" or "tiff")
        {
            using var tiff = Tiff.Open(path, "w"); tiff.SetField(TiffTag.IMAGEWIDTH, 8); tiff.SetField(TiffTag.IMAGELENGTH, 8); tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3); tiff.SetField(TiffTag.BITSPERSAMPLE, 8); tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB); tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG); tiff.SetField(TiffTag.ROWSPERSTRIP, 8); tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
            for (var y = 0; y < 8; y++) Assert.True(tiff.WriteScanline(Enumerable.Repeat((byte)120, 24).ToArray(), y)); return;
        }
        if (extension == "bmp")
        {
            using var output = new BinaryWriter(File.Create(path)); output.Write((ushort)0x4d42); output.Write(54 + 8 * 8 * 3); output.Write(0); output.Write(54); output.Write(40); output.Write(8); output.Write(8); output.Write((ushort)1); output.Write((ushort)24); output.Write(0); output.Write(8 * 8 * 3); output.Write(new byte[16]); output.Write(Enumerable.Repeat((byte)120, 8 * 8 * 3).ToArray()); return;
        }
        using var image = new SKBitmap(8, 8); image.Erase(SKColors.Green);
        using var bytes = image.Encode(extension is "jpg" or "jpeg" ? SKEncodedImageFormat.Jpeg : extension == "webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, 90); File.WriteAllBytes(path, bytes.ToArray());
    }
    private sealed class External : IExternalLauncher, ILocalFileLauncher
    {
        public List<string> Opened = [];
        public Task OpenFileAsync(string file, CancellationToken ct = default) { Opened.Add(file); return Task.CompletedTask; }
        public Task OpenUriAsync(Uri uri, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Service : FakeLauncherService, IInstanceWorkspaceService
    {
        public IReadOnlyList<InstanceContentEntry> Files = [];
        public Task<IReadOnlyList<InstanceContentEntry>> ListInstanceContentAsync(string id, string kind, CancellationToken ct = default) => Task.FromResult(kind == "screenshots" ? Files : []);
        public Task UpdateInstanceAsync(string id, InstanceEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportInstanceContentAsync(string id, string kind, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetInstanceModEnabledAsync(string id, string path, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ExportInstanceAsync(string id, bool worlds, CancellationToken ct = default) => Task.FromResult("fixture.zip");
    }
}
