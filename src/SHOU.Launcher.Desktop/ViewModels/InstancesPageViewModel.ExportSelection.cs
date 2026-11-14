using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ExportEntryChoice(InstanceExportEntry entry, bool selected, Action<string, bool> select, Func<string, Task> browse) : ObservableObject
{
    private bool isSelected = selected;
    public string Name => entry.Name;
    public string RelativePath => entry.RelativePath;
    public bool Directory => entry.Directory;
    public bool CanSelect => entry.CanSelect;
    public string? Reason => entry.Reason;
    public string Icon => Directory ? "folder" : "file";
    public bool Selected { get => isSelected; set { if (entry.CanSelect && SetProperty(ref isSelected, value)) select(RelativePath, value); } }
    public AsyncRelayCommand OpenCommand { get; } = new(() => browse(entry.RelativePath), () => entry.Directory && entry.CanSelect);
}
public sealed record ExportSelectedPath(string RelativePath, RelayCommand RemoveCommand);

public sealed partial class InstancesPageViewModel
{
    private CancellationTokenSource? exportBrowseCancellation;
    private int exportBrowseGeneration;
    private string? exportBrowseInstance, exportBrowseGameDirectory;
    private string exportBrowseDirectory = "", exportBrowseFeedback = "";
    private bool exportBrowseLoaded, exportBrowseBusy;
    public bool ExportBrowserAvailable => Service is IInstanceExportSelectionService;
    public string ExportBrowseDirectory => exportBrowseDirectory.Length == 0 ? "/" : exportBrowseDirectory;
    public string ExportBrowseFeedback => exportBrowseFeedback;
    public bool IsExportBrowseBusy => exportBrowseBusy;
    public string ExportSelectedSummary => Localization.UiLocalization.Instance.Format("另选 {0} 项文件或目录", ExportSelectedPaths.Count);
    public ObservableCollection<ExportEntryChoice> ExportEntries { get; } = [];
    public ObservableCollection<ExportSelectedPath> ExportSelectedPaths { get; } = [];
    public AsyncRelayCommand RefreshExportEntriesCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportParentDirectoryCommand { get; private set; } = null!;
    public RelayCommand ClearExportPathsCommand { get; private set; } = null!;
    private void InitializeExportSelection()
    {
        RefreshExportEntriesCommand = AsyncCommand(() => ReadExportEntriesAsync(exportBrowseDirectory), () => CanEdit && IsExport && ExportBrowserAvailable && !exportBrowseBusy);
        ExportParentDirectoryCommand = AsyncCommand(() => ReadExportEntriesAsync(exportBrowseDirectory.Contains('/') ? exportBrowseDirectory[..exportBrowseDirectory.LastIndexOf('/')] : ""), () => CanEdit && IsExport && ExportBrowserAvailable && !exportBrowseBusy && exportBrowseDirectory.Length > 0);
        ClearExportPathsCommand = Command(() => { ExportSelectedPaths.Clear(); RefreshExportRowSelection(); NotifyExportBrowser(); }, () => CanEdit && ExportSelectedPaths.Count > 0);
    }
    private void CancelExportBrowse()
    { ++exportBrowseGeneration; exportBrowseCancellation?.Cancel(); exportBrowseCancellation?.Dispose(); exportBrowseCancellation = null; exportBrowseBusy = false; }
    private void ExportNavigationChanged()
    {
        if (!_workspaceReady || _workspaceDisposed) return;
        if (SelectedInstance?.Id != exportBrowseInstance || SelectedInstance?.GameDirectory != exportBrowseGameDirectory)
        {
            CancelExportBrowse(); exportBrowseInstance = SelectedInstance?.Id; exportBrowseGameDirectory = SelectedInstance?.GameDirectory;
            exportBrowseDirectory = ""; exportBrowseLoaded = false; ExportEntries.Clear(); ExportSelectedPaths.Clear(); exportBrowseFeedback = ""; NotifyExportBrowser();
        }
        if (!IsExport || !ReferenceEquals(Owner.SelectedPage, this)) { CancelExportBrowse(); return; }
        if (!exportBrowseLoaded && !exportBrowseBusy && ExportBrowserAvailable) _ = ReadExportEntriesAsync(exportBrowseDirectory);
    }
    private async Task ReadExportEntriesAsync(string directory)
    {
        if (_workspaceDisposed || !IsExport || !ReferenceEquals(Owner.SelectedPage, this) || SelectedInstance is not { } instance || Service is not IInstanceExportSelectionService service) return;
        CancelExportBrowse(); var generation = exportBrowseGeneration; var source = new CancellationTokenSource(); exportBrowseCancellation = source;
        exportBrowseBusy = true; exportBrowseFeedback = ""; NotifyExportBrowser();
        bool Context() => !_workspaceDisposed && IsExport && ReferenceEquals(Owner.SelectedPage, this) && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory;
        bool Current() => generation == exportBrowseGeneration && Context();
        try
        {
            var rows = await service.GetInstanceExportEntriesAsync(instance.Id, directory, source.Token);
            if (!Current()) return;
            exportBrowseDirectory = directory; exportBrowseLoaded = true; ExportEntries.Clear();
            foreach (var row in rows) ExportEntries.Add(new(row, ExportSelectedPaths.Any(p => p.RelativePath == row.RelativePath), (path, selected) =>
            {
                if (!Context() || !CanEdit || exportBrowseDirectory != directory) return;
                SetExportPath(path, selected);
            }, path => Context() && CanEdit && exportBrowseDirectory == directory ? ReadExportEntriesAsync(path) : Task.CompletedTask));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { if (Current()) exportBrowseFeedback = Localization.UiLocalization.Instance.Text("导出目录读取失败：") + UiPrivacy.Redact(ex.Message); }
        finally { if (Current()) { exportBrowseBusy = false; NotifyExportBrowser(); } if (ReferenceEquals(exportBrowseCancellation, source)) exportBrowseCancellation = null; source.Dispose(); }
    }
    private void SetExportPath(string path, bool selected)
    {
        var current = ExportSelectedPaths.FirstOrDefault(p => p.RelativePath == path);
        var id = SelectedInstance?.Id; var directory = SelectedInstance?.GameDirectory;
        if (selected && current is null) ExportSelectedPaths.Add(new(path, new RelayCommand(() =>
        { if (CanEdit && SelectedInstance?.Id == id && SelectedInstance?.GameDirectory == directory) { SetExportPath(path, false); RefreshExportRowSelection(); } })));
        else if (!selected && current is not null) ExportSelectedPaths.Remove(current);
        NotifyExportBrowser();
    }
    private void RefreshExportRowSelection()
    { foreach (var row in ExportEntries) row.Selected = ExportSelectedPaths.Any(p => p.RelativePath == row.RelativePath); }
    private void NotifyExportBrowser()
    {
        foreach (var name in new[] { nameof(ExportBrowseDirectory), nameof(ExportBrowseFeedback), nameof(IsExportBrowseBusy), nameof(ExportSelectedSummary), nameof(ExportBrowserAvailable) }) OnPropertyChanged(name);
        RefreshExportEntriesCommand?.NotifyCanExecuteChanged(); ExportParentDirectoryCommand?.NotifyCanExecuteChanged(); ClearExportPathsCommand?.NotifyCanExecuteChanged();
    }
    private IReadOnlyList<string> MergeExportPaths(IEnumerable<string> builtIn)
    {
        var paths = builtIn.Concat(ExportSelectedPaths.Select(p => p.RelativePath)).Distinct(StringComparer.Ordinal).ToArray();
        return paths.Where(p => !paths.Any(parent => parent != p && p.StartsWith(parent + "/", StringComparison.Ordinal))).ToArray();
    }
}
