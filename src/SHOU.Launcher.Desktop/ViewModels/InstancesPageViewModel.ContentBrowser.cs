using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private string contentDirectory = "";
    private int contentDirectoryGeneration;
    public string ContentDirectory => contentDirectory;
    public bool CanBrowseContentDirectories => IsContentList && SelectedSection.Key == "schematics" && Service is IInstanceContentBrowserService;
    public bool IsContentSubdirectory => CanBrowseContentDirectories && contentDirectory.Length > 0;
    public string ContentDirectoryCaption => IsContentSubdirectory ? "schematics/" + contentDirectory : "schematics";
    public AsyncRelayCommand ParentContentDirectoryCommand { get; private set; } = null!;
    private void InitializeContentBrowser()
        => ParentContentDirectoryCommand = AsyncCommand(() => NavigateContentDirectoryAsync(contentDirectory.Contains('/') ? contentDirectory[..contentDirectory.LastIndexOf('/')] : ""), () => CanEdit && IsContentSubdirectory && !RefreshContentCommand.IsRunning);
    private void ResetContentDirectory()
    {
        contentDirectory = ""; ++contentDirectoryGeneration; NotifyContentDirectory();
    }
    private void NotifyContentDirectory()
    {
        foreach (var name in new[] { nameof(ContentDirectory), nameof(ContentDirectoryCaption), nameof(CanBrowseContentDirectories), nameof(IsContentSubdirectory) }) OnPropertyChanged(name);
        ParentContentDirectoryCommand?.NotifyCanExecuteChanged();
    }
    public Task OpenContentFolderAsync(InstanceContentRowViewModel row)
    {
        if (!CanBrowseContentDirectories || !CurrentContentRow(row) || !row.Entry.Directory || !row.Entry.RelativePath.StartsWith("schematics/", StringComparison.Ordinal)) return Task.CompletedTask;
        return NavigateContentDirectoryAsync(row.Entry.RelativePath[11..]);
    }
    private async Task NavigateContentDirectoryAsync(string directory)
    {
        if (!CanBrowseContentDirectories || !CanEdit || RefreshContentCommand.IsRunning) return;
        contentDirectory = directory; ++contentDirectoryGeneration;
        ContentSearchText = ""; ContentEntries.Clear(); SelectedContent = null; ClearModUpdates(); Feedback = "";
        NotifyContentDirectory(); NotifyContentFilter(); await RefreshContentCommand.ExecuteAsync(null);
    }
}
