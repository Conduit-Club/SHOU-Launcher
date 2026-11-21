using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceScreenshotPreview(string instanceId, InstanceContentEntry file, Bitmap? preview, AsyncRelayCommand reveal, AsyncRelayCommand remove, AsyncRelayCommand open) : IDisposable
{
    public string InstanceId => instanceId;
    public string RelativePath => file.RelativePath;
    public string Name => file.Name;
    public Bitmap? Preview { get; } = preview;
    public bool HasPreview => Preview is not null;
    public AsyncRelayCommand RevealCommand { get; } = reveal;
    public AsyncRelayCommand RemoveCommand { get; } = remove;
    public AsyncRelayCommand OpenCommand { get; } = open;
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
    private IReadOnlyList<InstanceContentEntry> screenshotFiles = [];
    private CancellationTokenSource? screenshotCancellation;
    private bool loadingScreenshots;
    public bool HasMoreScreenshots => ScreenshotPreviews.Count < screenshotFiles.Count;
    public bool HasTruncatedScreenshots => HasMoreScreenshots;
    public bool IsLoadingScreenshots => loadingScreenshots;
    public AsyncRelayCommand LoadMoreScreenshotsCommand { get; private set; } = null!;
    public bool IsModsSection => IsFileSection && SelectedSection.Key == "mods";
    public string OpenContentFolderCaption => "打开" + SectionTitle + "文件夹";
    public string ScreenshotSummary => Localization.UiLocalization.Instance.Format("已显示 {0}/{1} 张截图", ScreenshotPreviews.Count, screenshotFiles.Count);

    private void ClearScreenshotPreviews()
    {
        _screenshotGeneration++; screenshotCancellation?.Cancel(); screenshotCancellation?.Dispose(); screenshotCancellation = null; screenshotFiles = []; loadingScreenshots = false;
        Interlocked.Increment(ref _screenshotCopyTicket);
        foreach (var item in ScreenshotPreviews) item.Dispose();
        ScreenshotPreviews.Clear(); NotifyScreenshotPaging();
    }
    private void NotifyScreenshotPaging()
    {
        foreach (var name in new[] { nameof(ScreenshotSummary), nameof(HasMoreScreenshots), nameof(HasTruncatedScreenshots), nameof(IsLoadingScreenshots) }) OnPropertyChanged(name);
        LoadMoreScreenshotsCommand?.NotifyCanExecuteChanged();
    }
    private async Task LoadScreenshotPreviewsAsync(string instanceId, IReadOnlyList<InstanceContentEntry> files, CancellationToken ct)
    {
        ClearScreenshotPreviews(); var generation = _screenshotGeneration;
        var trashEntries = Service is IScreenshotTrashService trash ? await trash.ListScreenshotTrashAsync(instanceId, ct) : [];
        if (generation != _screenshotGeneration || SelectedInstance?.Id != instanceId || !IsScreenshots) return;
        _screenshotTrash = trashEntries; _trashInstanceId = instanceId; RestoreScreenshotCommand.NotifyCanExecuteChanged();
        screenshotFiles = files.Where(f => !f.Directory).OrderByDescending(f => f.Created ?? f.Modified).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        screenshotCancellation = new(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, screenshotCancellation.Token);
        await AppendScreenshotsAsync(linked.Token);
    }
    private async Task AppendScreenshotsAsync(CancellationToken ct)
    {
        if (loadingScreenshots || SelectedInstance is not { } instance || !IsScreenshots) return;
        var generation = _screenshotGeneration; var batch = screenshotFiles.Skip(ScreenshotPreviews.Count).Take(20).ToArray();
        bool Current() => !_workspaceDisposed && generation == _screenshotGeneration && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && IsScreenshots && ReferenceEquals(Owner.SelectedPage, this);
        loadingScreenshots = true; NotifyScreenshotPaging();
        try
        {
            foreach (var file in batch)
            {
                ct.ThrowIfCancellationRequested(); if (!Current()) return;
                var preview = await Task.Run(() => ScreenshotImages.Read(file, true, ct), ct);
                if (ct.IsCancellationRequested || !Current()) { preview?.Dispose(); ct.ThrowIfCancellationRequested(); return; }
                async Task ReadFile(Func<InstanceContentEntry, CancellationToken, Task> action)
                {
                    if (!Current()) return;
                    await Owner.RunControlAsync("打开截图", async token =>
                    {
                        var files = await ((IInstanceWorkspaceService)Service).ListInstanceContentAsync(instance.Id, "screenshots", token);
                        if (!Current()) return;
                        var found = files.SingleOrDefault(f => f.RelativePath == file.RelativePath) ?? throw new FileNotFoundException("截图已移动，请刷新列表。");
                        await action(found, token);
                    });
                }
                ScreenshotPreviews.Add(new(instance.Id, file, preview,
                    new AsyncRelayCommand(() => ReadFile((found, token) => Owner.ExternalLauncher.RevealFileAsync(found.FullPath, token))),
                    new AsyncRelayCommand(async () =>
                    {
                        if (!Current() || Service is not IScreenshotTrashService trashService) return;
                        if (await Owner.RunAsync("移入截图回收站", token => trashService.MoveScreenshotToTrashAsync(instance.Id, file.RelativePath, token)) && Current())
                        { Feedback = "截图已移入实例回收站，可恢复最近删除。"; await RefreshContentCommand.ExecuteAsync(null); }
                    }, () => CanEdit && Service is IScreenshotTrashService && Current()),
                    new AsyncRelayCommand(() => ReadFile((found, token) => ((ILocalFileLauncher)Owner.ExternalLauncher).OpenFileAsync(found.FullPath, token)), () => Current() && Owner.ExternalLauncher is ILocalFileLauncher)));
            }
        }
        finally { if (generation == _screenshotGeneration) { loadingScreenshots = false; NotifyScreenshotPaging(); } }
    }
    private void InitializeScreenshotActions()
    {
        LoadMoreScreenshotsCommand = AsyncCommand(async () =>
        { try { if (screenshotCancellation is { } source) await AppendScreenshotsAsync(source.Token); } catch (OperationCanceledException) { } }, () => !_workspaceDisposed && IsScreenshots && HasMoreScreenshots && !loadingScreenshots && ReferenceEquals(Owner.SelectedPage, this));
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
    private static Bitmap? DecodeScreenshot(InstanceContentEntry file, bool thumbnail = true) => ScreenshotImages.Read(file, thumbnail);
    public void Dispose() { _workspaceDisposed = true; CancelExportBrowse(); CancelServerStatus(); foreach (var draft in _worldDrafts.Values) foreach (var row in draft.Servers) row.Dispose(); StopModScan(); StopModRelations(); _pendingContentRefresh = false; DisposeComponents(); ClearScreenshotPreviews(); }
}
