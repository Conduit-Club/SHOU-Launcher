using System.Collections.ObjectModel;
using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceScreenshotPreview(string instanceId, InstanceContentEntry file, Bitmap? preview, AsyncRelayCommand reveal, AsyncRelayCommand remove) : IDisposable
{
    public string InstanceId => instanceId;
    public string RelativePath => file.RelativePath;
    public string Name => file.Name;
    public Bitmap? Preview { get; } = preview;
    public bool HasPreview => Preview is not null;
    public AsyncRelayCommand RevealCommand { get; } = reveal;
    public AsyncRelayCommand RemoveCommand { get; } = remove;
    public void Dispose() => Preview?.Dispose();
}

public sealed partial class InstancesPageViewModel : IDisposable
{
    private int _screenshotGeneration;
    private long _screenshotCopyTicket;
    private readonly SemaphoreSlim _screenshotClipboardGate = new(1, 1);
    private string? _trashInstanceId;
    private IReadOnlyList<ScreenshotTrashEntry> _screenshotTrash = [];
    public AsyncRelayCommand RestoreScreenshotCommand { get; private set; } = null!;
    public ObservableCollection<InstanceScreenshotPreview> ScreenshotPreviews { get; } = [];
    public bool IsScreenshots => IsFileSection && SelectedSection.Key == "screenshots";
    public bool IsContentList => IsFileSection && !IsScreenshots;
    public bool HasTruncatedScreenshots => ContentEntries.Count > 60;
    public bool IsModsSection => IsFileSection && SelectedSection.Key == "mods";
    public string OpenContentFolderCaption => "打开" + SectionTitle + "文件夹";
    public string ScreenshotSummary => ContentEntries.Count > 60 ? $"共 {ContentEntries.Count} 张截图，预览前 60 张；其余可在文件夹中查看。" : $"共 {ContentEntries.Count} 张截图";

    private void ClearScreenshotPreviews()
    {
        _screenshotGeneration++;
        Interlocked.Increment(ref _screenshotCopyTicket);
        foreach (var item in ScreenshotPreviews) item.Dispose();
        ScreenshotPreviews.Clear();
    }
    private async Task LoadScreenshotPreviewsAsync(string instanceId, IReadOnlyList<InstanceContentEntry> files, CancellationToken ct)
    {
        ClearScreenshotPreviews(); var generation = _screenshotGeneration;
        var trashEntries = Service is IScreenshotTrashService trash ? await trash.ListScreenshotTrashAsync(instanceId, ct) : [];
        if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) return;
        _screenshotTrash = trashEntries; _trashInstanceId = instanceId; RestoreScreenshotCommand.NotifyCanExecuteChanged();
        foreach (var file in files.Where(f => !f.Directory).Take(60))
        {
            ct.ThrowIfCancellationRequested();
            if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) return;
            var preview = await Task.Run(() => DecodeScreenshot(file), ct);
            if (ct.IsCancellationRequested) { preview?.Dispose(); ct.ThrowIfCancellationRequested(); }
            if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) { preview?.Dispose(); return; }
            ScreenshotPreviews.Add(new(instanceId, file, preview, new AsyncRelayCommand(() => Owner.RunControlAsync("在文件夹中显示截图", async token =>
            {
                var current = await ((IInstanceWorkspaceService)Service).ListInstanceContentAsync(instanceId, "screenshots", token);
                var found = current.SingleOrDefault(f => f.RelativePath == file.RelativePath) ?? throw new FileNotFoundException("截图已移动，请刷新列表。");
                await Owner.ExternalLauncher.RevealFileAsync(found.FullPath, token);
            })), new AsyncRelayCommand(async () =>
            {
                if (SelectedInstance?.Id != instanceId || !IsScreenshots || Service is not IScreenshotTrashService trashService) return;
                if (await Owner.RunAsync("移入截图回收站", token => trashService.MoveScreenshotToTrashAsync(instanceId, file.RelativePath, token)))
                { Feedback = "截图已移入实例回收站，可恢复最近删除。"; await RefreshContentCommand.ExecuteAsync(null); }
            }, () => CanEdit && Service is IScreenshotTrashService && SelectedInstance?.Id == instanceId)));
        }
        OnPropertyChanged(nameof(ScreenshotSummary));
    }
    private void InitializeScreenshotActions()
    {
        RestoreScreenshotCommand = AsyncCommand(async () =>
        {
            var instanceId = _trashInstanceId!; var entry = _screenshotTrash[0];
            if (await Owner.RunAsync("恢复截图", ct => ((IScreenshotTrashService)Service).RestoreScreenshotAsync(instanceId, entry.Id, ct)))
            { Feedback = "已恢复截图，原位置的同名文件不会被覆盖。"; await RefreshContentCommand.ExecuteAsync(null); }
        }, () => CanEdit && IsScreenshots && SelectedInstance?.Id == _trashInstanceId && _screenshotTrash.Count > 0 && Service is IScreenshotTrashService);
    }
    public Task CopyScreenshotAsync(InstanceScreenshotPreview item, IClipboard clipboard)
    {
        var ticket = Interlocked.Increment(ref _screenshotCopyTicket); var generation = _screenshotGeneration;
        bool Current() => !_workspaceDisposed && ticket == Interlocked.Read(ref _screenshotCopyTicket) && generation == _screenshotGeneration && SelectedInstance?.Id == item.InstanceId && IsScreenshots;
        return Owner.RunControlAsync("复制截图", async ct =>
        {
            if (!Current()) return;
            var files = await ((IInstanceWorkspaceService)Service).ListInstanceContentAsync(item.InstanceId, "screenshots", ct);
            if (!Current()) return;
            var file = files.SingleOrDefault(f => f.RelativePath == item.RelativePath) ?? throw new FileNotFoundException("截图已移动，请刷新。");
            var bitmap = await Task.Run(() => DecodeScreenshot(file, false), ct) ?? throw new InvalidDataException("截图无法安全解码。");
            var transfer = new OwnedBitmapTransfer(bitmap); var transferred = false;
            try
            {
                await _screenshotClipboardGate.WaitAsync(ct);
                try
                {
                    if (!Current()) return;
                    ct.ThrowIfCancellationRequested(); await clipboard.SetDataAsync(transfer); transferred = true;
                    if (Current()) Feedback = "图片已复制到剪贴板。";
                }
                finally { _screenshotClipboardGate.Release(); }
            }
            finally { if (!transferred) transfer.Dispose(); }
        });
    }
    private sealed class OwnedBitmapTransfer : IAsyncDataTransfer, IDataTransfer
    {
        private readonly DataTransfer data = new(); private Bitmap? bitmap;
        public OwnedBitmapTransfer(Bitmap image) { bitmap = image; data.Add(DataTransferItem.Create(DataFormat.Bitmap, image)); }
        public IReadOnlyList<DataFormat> Formats => data.Formats;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => ((IAsyncDataTransfer)data).Items;
        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => ((IDataTransfer)data).Items;
        public void Dispose() => Interlocked.Exchange(ref bitmap, null)?.Dispose();
    }
    private static Bitmap? DecodeScreenshot(InstanceContentEntry file, bool thumbnail = true)
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
            return !thumbnail ? new Bitmap(stream) : (long)width * 270 >= (long)height * 480 ? Bitmap.DecodeToWidth(stream, 480) : Bitmap.DecodeToHeight(stream, 270);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }
    public void Dispose() { _workspaceDisposed = true; _pendingContentRefresh = false; ClearScreenshotPreviews(); }
}
