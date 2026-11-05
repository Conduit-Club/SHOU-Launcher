using System.Collections.ObjectModel;
using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceScreenshotPreview(InstanceContentEntry file, Bitmap? preview, AsyncRelayCommand reveal) : IDisposable
{
    public string Name => file.Name;
    public Bitmap? Preview { get; } = preview;
    public bool HasPreview => Preview is not null;
    public AsyncRelayCommand RevealCommand { get; } = reveal;
    public void Dispose() => Preview?.Dispose();
}

public sealed partial class InstancesPageViewModel : IDisposable
{
    private int _screenshotGeneration;
    public ObservableCollection<InstanceScreenshotPreview> ScreenshotPreviews { get; } = [];
    public bool IsScreenshots => IsFileSection && SelectedSection.Key == "screenshots";
    public bool IsContentList => IsFileSection && !IsScreenshots;
    public string ScreenshotSummary => ContentEntries.Count > 60 ? $"共 {ContentEntries.Count} 张截图，预览前 60 张；其余可在文件夹中查看。" : $"共 {ContentEntries.Count} 张截图";

    private void ClearScreenshotPreviews()
    {
        _screenshotGeneration++;
        foreach (var item in ScreenshotPreviews) item.Dispose();
        ScreenshotPreviews.Clear();
    }
    private async Task LoadScreenshotPreviewsAsync(string instanceId, IReadOnlyList<InstanceContentEntry> files, CancellationToken ct)
    {
        ClearScreenshotPreviews(); var generation = _screenshotGeneration;
        foreach (var file in files.Where(f => !f.Directory).Take(60))
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) return;
            var preview = await Task.Run(() => DecodeScreenshot(file), ct);
            if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) { preview?.Dispose(); return; }
            ScreenshotPreviews.Add(new(file, preview, new AsyncRelayCommand(() => Owner.RunControlAsync("在文件夹中显示截图", async token =>
            {
                var current = await ((IInstanceWorkspaceService)Service).ListInstanceContentAsync(instanceId, "screenshots", token);
                var found = current.SingleOrDefault(f => f.RelativePath == file.RelativePath) ?? throw new FileNotFoundException("截图已移动，请刷新列表。");
                await Owner.ExternalLauncher.RevealFileAsync(found.FullPath, token);
            }))));
        }
        OnPropertyChanged(nameof(ScreenshotSummary));
    }
    private static Bitmap? DecodeScreenshot(InstanceContentEntry file)
    {
        try
        {
            var info = new FileInfo(file.FullPath);
            if (!info.Exists || info.Length is <= 0 or > 32 * 1024 * 1024 || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Minecraft screenshots are PNG. Bound both preview axes before invoking the decoder.
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header[12..16].SequenceEqual("IHDR"u8)) return null;
            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]); var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            if (width is 0 or > 16384 || height is 0 or > 16384 || (long)width * height > 64 * 1024 * 1024) return null;
            stream.Position = 0;
            return (long)width * 270 >= (long)height * 480 ? Bitmap.DecodeToWidth(stream, 480) : Bitmap.DecodeToHeight(stream, 270);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }
    public void Dispose() => ClearScreenshotPreviews();
}
