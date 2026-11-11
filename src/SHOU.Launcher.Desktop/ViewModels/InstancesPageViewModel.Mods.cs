using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private string contentSearchText = "";
    public string ContentSearchText { get => contentSearchText; set { if (SetProperty(ref contentSearchText, value ?? "")) NotifyContentFilter(); } }
    public IReadOnlyList<InstanceContentEntry> FilteredContentEntries => ContentEntries.Where(row => ContentSearchText.Trim().Length == 0 ||
        (row.DisplayName + " " + row.Name + " " + row.VersionLabel).Contains(ContentSearchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    private void NotifyContentFilter()
    {
        OnPropertyChanged(nameof(FilteredContentEntries)); OnPropertyChanged(nameof(IsContentEmpty)); OnPropertyChanged(nameof(ContentEmptyTitle));
        if (SelectedContent is not null && !FilteredContentEntries.Contains(SelectedContent)) SelectedContent = null;
    }
    public ObservableCollection<InstanceModUpdate> ModUpdates { get; } = [];
    private InstanceModUpdate? selectedModUpdate;
    private string modUpdateStatus = "";
    private bool confirmModUpdate;
    public InstanceModUpdate? SelectedModUpdate { get => selectedModUpdate; set { if (SetProperty(ref selectedModUpdate, value)) { ConfirmModUpdate = false; NotifyCommands(); } } }
    public string ModUpdateStatus { get => modUpdateStatus; private set { SetProperty(ref modUpdateStatus, value); OnPropertyChanged(nameof(HasModUpdateStatus)); } }
    public bool HasModUpdateStatus => ModUpdateStatus.Length > 0;
    public bool HasModUpdates => ModUpdates.Count > 0;
    public bool ConfirmModUpdate { get => confirmModUpdate; set { SetProperty(ref confirmModUpdate, value); NotifyCommands(); } }
    public RelayCommand DownloadInstanceResourcesCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckModUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyModUpdateCommand { get; private set; } = null!;
    private void InitializeModManagement()
    {
        ContentEntries.CollectionChanged += (_, _) => NotifyContentFilter();
        DownloadInstanceResourcesCommand = Command(() =>
        {
            Owner.Downloads.OpenInstanceResourceBrowser(SelectedInstance!, SelectedSection.Key);
            Owner.SelectedPage = Owner.Downloads;
        }, () => CanEdit && SelectedInstance is not null && SelectedSection.Key is "mods" or "resourcepacks" or "shaders");
        CheckModUpdatesCommand = AsyncCommand(CheckModUpdatesAsync, () => CanEdit && !IsSelectedInstanceRunning && IsModsSection && SelectedInstance is not null && Service is IInstanceModUpdateService);
        ApplyModUpdateCommand = AsyncCommand(ApplyModUpdateAsync, () => CanEdit && !IsSelectedInstanceRunning && IsModsSection && ConfirmModUpdate && SelectedModUpdate?.InstanceId == SelectedInstance?.Id && Service is IInstanceModUpdateService);
    }
    private void ClearModUpdates()
    { ModUpdates.Clear(); SelectedModUpdate = null; ModUpdateStatus = ""; OnPropertyChanged(nameof(HasModUpdates)); }
    private async Task CheckModUpdatesAsync()
    {
        var instance = SelectedInstance!; InstanceModUpdateCheck? result = null; ClearModUpdates();
        if (!await Owner.RunAsync("检查模组更新", async ct => result = await ((IInstanceModUpdateService)Service).CheckInstanceModUpdatesAsync(instance.Id, ct))) return;
        if (SelectedInstance?.Id != instance.Id || !IsModsSection) return;
        foreach (var update in result!.Updates) ModUpdates.Add(update);
        SelectedModUpdate = ModUpdates.FirstOrDefault(); OnPropertyChanged(nameof(HasModUpdates));
        ModUpdateStatus = $"已检查 {result.CheckedFiles} 个模组，发现 {result.Updates.Count} 项同游戏版本/加载器的正式版更新。" +
            (result.Notices.Count > 0 ? $" {result.Notices.Count} 项未能核实，未计为最新版。\n" + string.Join("\n", result.Notices.Take(5)) : "");
    }
    private async Task ApplyModUpdateAsync()
    {
        var update = SelectedModUpdate!; InstanceModUpdateResult? result = null;
        if (!await Owner.RunAsync("更新模组并备份", async ct => result = await ((IInstanceModUpdateService)Service).ApplyInstanceModUpdateAsync(update.Token, ct))) return;
        if (SelectedInstance?.Id != update.InstanceId || !IsModsSection) return;
        ModUpdates.Remove(update); SelectedModUpdate = ModUpdates.FirstOrDefault(); OnPropertyChanged(nameof(HasModUpdates));
        ModUpdateStatus = $"已更新 {update.Name}，安装 {result!.InstalledFiles} 项文件；原模组及安装清单备份至 {result.BackupDirectory}。";
        await RefreshContentCommand.ExecuteAsync(null);
    }
}

public sealed partial class DownloadsPageViewModel
{
    public void OpenInstanceResourceBrowser(GameInstance instance, string section)
    {
        var target = ContentInstallTargets.FirstOrDefault(t => t.Instance.Id == instance.Id && t.Instance.GameDirectory == instance.GameDirectory)
            ?? throw new InvalidOperationException("目标实例已改变，请重新选择。");
        SelectedInstallTarget = target; _isConfiguration = false; _isResourceDetail = false; _isPackageDetail = false;
        SelectCategory(section switch { "mods" => "mod", "resourcepacks" => "resourcepack", "shaders" => "shader", _ => throw new ArgumentException("此类别没有资源目录。") });
        GameVersionFilter = InstanceIdentity.GameVersion(instance);
        LoaderFilter = LoaderFilters.FirstOrDefault(l => l.Equals(instance.Loader, StringComparison.OrdinalIgnoreCase)) ?? "任意";
    }
}
