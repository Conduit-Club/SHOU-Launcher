using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private InstanceContentRowViewModel? detailRow;
    private bool IsResourceTrashSection => IsContentList && SelectedSection.Key is "resourcepacks" or "shaders" or "schematics";
    public bool CanReadContentTrash => SelectedInstance is not null &&
        (IsModsSection && Service is IInstanceModManagementService || IsResourceTrashSection && Service is IInstanceContentTrashService);
    public bool CanTrashContent => CanTrashMods || !_workspaceDisposed && ReferenceEquals(Owner.SelectedPage, this) && CanEdit && !IsSelectedInstanceRunning && IsResourceTrashSection && Service is IInstanceContentTrashService;
    public string ContentTrashTitle => Localization.UiLocalization.Instance.Text(IsModsSection ? "模组回收站" : "资源回收站");
    public bool CanOpenContentProject => CanEdit && detailRow is { Project: not null } row && CurrentContentRow(row) && Owner.Downloads.CanQuery;
    public AsyncRelayCommand OpenContentProjectCommand { get; private set; } = null!;
    public RelayCommand CloseContentTrashCommand { get; private set; } = null!;
    private void ShowContentDetails(InstanceContentRowViewModel row)
    {
        if (!CurrentContentRow(row)) return;
        detailRow = row; ModDetail = row.Detail; OnPropertyChanged(nameof(CanOpenContentProject)); NotifyCommands();
    }
    private async Task OpenContentProjectAsync()
    {
        if (!CanOpenContentProject || detailRow?.Project is not { } project || SelectedInstance is not { } instance) return;
        var kind = SelectedSection.Key; detailRow = null; ModDetail = "";
        await Owner.Downloads.OpenInstalledResourceProjectAsync(instance, kind, project);
    }
    private async Task RestoreContentTrashAsync()
    {
        if (!CanTrashContent || SelectedInstance is not { } instance) return;
        var kind = SelectedSection.Key; var trash = SelectedModTrash ?? ModTrash.FirstOrDefault();
        if (trash is null || !ModTrash.Any(t => t.Id == trash.Id)) return;
        if (await Owner.RunAsync(kind == "mods" ? "恢复模组" : "恢复资源", async ct =>
        {
            await StopModScanAsync();
            if (kind == "mods") await ((IInstanceModManagementService)Service).RestoreInstanceModsAsync(instance.Id, trash.Id, ct);
            else await ((IInstanceContentTrashService)Service).RestoreInstanceContentAsync(instance.Id, kind, trash.Id, ct);
        }) && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && SelectedSection.Key == kind && ReferenceEquals(Owner.SelectedPage, this))
        { await RefreshContentCommand.ExecuteAsync(null); await ReadModTrashAsync(); }
    }
}

public sealed partial class DownloadsPageViewModel
{
    private (string Id, string Directory, string Kind)? instanceResourceReturn;
    public async Task OpenInstalledResourceProjectAsync(GameInstance instance, string kind, ContentProject project)
    {
        if (!CanQuery || kind is not ("mods" or "resourcepacks" or "shaders")) return;
        OpenInstanceResourceBrowser(instance, kind);
        instanceResourceReturn = (instance.Id, instance.GameDirectory, kind);
        Owner.SelectedPage = this;
        await OpenProjectAsync(project, keepInstanceReturn: true);
    }
    private void ReturnToInstanceResource()
    {
        var target = instanceResourceReturn; instanceResourceReturn = null;
        if (target is { } origin && Owner.Instances.SelectedInstance is { } current && current.Id == origin.Id && current.GameDirectory == origin.Directory && Owner.Instances.SelectedSection.Key == origin.Kind)
            Owner.SelectedPage = Owner.Instances;
    }
}
