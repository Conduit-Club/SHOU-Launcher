using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    public ObservableCollection<ExistingInstanceCandidate> ExistingCandidates { get; } = [];
    private ExistingInstanceCandidate? existingCandidate;
    private bool folderBusy;
    public ExistingInstanceCandidate? ExistingCandidate { get => existingCandidate; set { SetProperty(ref existingCandidate, value); NotifyCommands(); } }
    public bool HasExistingCandidates => ExistingCandidates.Count > 0;
    public ObservableCollection<ExistingInstanceIssue> ExistingIssues { get; } = [];
    public bool HasExistingIssues => ExistingIssues.Count > 0;
    public bool CanBatchAttach => Service is IExistingInstanceBatchService;
    public string AttachAllCaption => $"接入全部（{ExistingCandidates.Count}）";
    public AsyncRelayCommand AttachAllExistingCommand { get; private set; } = null!;
    public bool IsFolderBusy { get => folderBusy; private set { SetProperty(ref folderBusy, value); NotifyCommands(); } }
    public AsyncRelayCommand AttachExistingCommand { get; private set; } = null!;
    public RelayCommand DismissExistingCommand { get; private set; } = null!;
    public RelayCommand ManageGameDirectoryCommand { get; private set; } = null!;
    private void InitializeExistingInstances()
    {
        ManageGameDirectoryCommand = Command(() => { Owner.Settings.SelectedSection = "manage"; Owner.ShowSettingsCommand.Execute(null); });
        DismissExistingCommand = Command(() => { ExistingCandidates.Clear(); ExistingCandidate = null; OnPropertyChanged(nameof(HasExistingCandidates)); });
        AttachAllExistingCommand = AsyncCommand(async () =>
        {
            var candidates = ExistingCandidates.ToArray(); IsFolderBusy = true;
            try
            {
                await Owner.RunControlAsync("接入实例目录", async ct =>
                {
                    var attached = await ((IExistingInstanceBatchService)Service).AttachExistingInstancesAsync(candidates, ct);
                    ExistingCandidates.Clear(); ExistingCandidate = null; OnPropertyChanged(nameof(HasExistingCandidates));
                    SearchText = ""; FavoritesOnly = false; InstanceCategoryFilter = "全部分类";
                    SelectedGameFolder = GameFolders.FirstOrDefault(f => SameFolder(f.Path, candidates[0].Origin.MinecraftRoot)) ?? GameFolders.First();
                    Feedback = $"已接入 {attached.Count} 个实例。";
                });
            }
            finally { IsFolderBusy = false; }
        }, () => CanEdit && !IsFolderBusy && HasExistingCandidates && CanBatchAttach);
        AttachExistingCommand = AsyncCommand(async () =>
        {
            var candidate = ExistingCandidate!; IsFolderBusy = true;
            try
            {
                await Owner.RunControlAsync("接入实例目录", async ct =>
                {
                    await ((IExistingInstanceService)Service).AttachExistingInstanceAsync(candidate, ct);
                    ExistingCandidates.Clear(); ExistingCandidate = null; OnPropertyChanged(nameof(HasExistingCandidates));
                    Feedback = "实例目录已接入。";
                });
            }
            finally { IsFolderBusy = false; }
        }, () => CanEdit && !IsFolderBusy && ExistingCandidate is not null && Service is IExistingInstanceService);
    }
    public async Task InspectExistingFolderAsync(string folder)
    {
        if (!CanEdit || IsFolderBusy || Service is not IExistingInstanceService existing) return;
        IsFolderBusy = true;
        try
        {
            await Owner.RunControlAsync("读取实例目录", async ct =>
            {
                ExistingInstanceInspection inspected = existing is IExistingInstanceBatchService batch ? await batch.InspectInstanceFolderDetailedAsync(folder, ct) : new(await existing.InspectInstanceFolderAsync(folder, ct), []);
                var candidates = inspected.Candidates.Where(c => !Owner.Snapshot.Instances.Any(i => i.Origin is { } origin && SameFolder(origin.VersionDirectory, c.Origin.VersionDirectory))).ToArray();
                ExistingCandidates.Clear(); foreach (var candidate in candidates) ExistingCandidates.Add(candidate);
                ExistingIssues.Clear(); foreach (var issue in inspected.Issues) ExistingIssues.Add(issue); OnPropertyChanged(nameof(HasExistingIssues)); OnPropertyChanged(nameof(AttachAllCaption));
                ExistingCandidate = ExistingCandidates.FirstOrDefault(); OnPropertyChanged(nameof(HasExistingCandidates));
                Feedback = candidates.Length == 0 ? inspected.Candidates.Count > 0 ? "所选目录中的实例已接入。" : "所选目录中没有有效的 Minecraft 实例。" : "";
            });
        }
        finally { IsFolderBusy = false; }
    }
    private bool useGlobalLaunchOptions = true;
    private GameProcessPriority instancePriority;
    private GameIpPreference instanceIp;
    private LauncherVisibility instanceVisibility;
    private string instanceTitle = "", instanceJvm = "";
    private bool instanceLockMemory;
    private bool instanceCustomWindow, instanceFullscreen;
    private decimal instanceWindowWidth = 854, instanceWindowHeight = 480;
    private string instanceCustomInfo = "", instanceGameArguments = "";
    public bool UseGlobalLaunchOptions { get => useGlobalLaunchOptions; set { if (SetProperty(ref useGlobalLaunchOptions, value)) _instanceDraftDirty = true; } }
    public GameProcessPriority InstancePriority { get => instancePriority; set { if (SetProperty(ref instancePriority, value)) _instanceDraftDirty = true; } }
    public GameIpPreference InstanceIpPreference { get => instanceIp; set { if (SetProperty(ref instanceIp, value)) _instanceDraftDirty = true; } }
    public LauncherVisibility InstanceVisibility { get => instanceVisibility; set { if (SetProperty(ref instanceVisibility, value)) _instanceDraftDirty = true; } }
    public string InstanceWindowTitle { get => instanceTitle; set { if (SetProperty(ref instanceTitle, value ?? "")) _instanceDraftDirty = true; } }
    public string InstanceJvmArguments { get => instanceJvm; set { if (SetProperty(ref instanceJvm, value ?? "")) _instanceDraftDirty = true; } }
    public bool InstanceLockMemory { get => instanceLockMemory; set { if (SetProperty(ref instanceLockMemory, value)) _instanceDraftDirty = true; } }
    public bool InstanceCustomWindow { get => instanceCustomWindow; set { if (SetProperty(ref instanceCustomWindow, value)) _instanceDraftDirty = true; } }
    public bool InstanceFullscreen { get => instanceFullscreen; set { if (SetProperty(ref instanceFullscreen, value)) _instanceDraftDirty = true; } }
    public decimal InstanceWindowWidth { get => instanceWindowWidth; set { if (SetProperty(ref instanceWindowWidth, value)) _instanceDraftDirty = true; } }
    public decimal InstanceWindowHeight { get => instanceWindowHeight; set { if (SetProperty(ref instanceWindowHeight, value)) _instanceDraftDirty = true; } }
    public string InstanceCustomInfo { get => instanceCustomInfo; set { if (SetProperty(ref instanceCustomInfo, value ?? "")) _instanceDraftDirty = true; } }
    public string InstanceGameArguments { get => instanceGameArguments; set { if (SetProperty(ref instanceGameArguments, value ?? "")) _instanceDraftDirty = true; } }
    private void LoadInstanceLaunchOptions(GameInstance instance, LauncherSettings settings)
    {
        UseGlobalLaunchOptions = instance.LaunchOptions is null or { InheritGlobal: true };
        var options = SHOU.Launcher.Core.Minecraft.GameSettingsPolicy.Options(instance, settings);
        InstancePriority = options.Priority; InstanceIpPreference = options.IpPreference; InstanceVisibility = options.Visibility; InstanceWindowTitle = options.WindowTitle; InstanceJvmArguments = options.JvmArguments; InstanceLockMemory = options.LockMemory;
        InstanceCustomWindow = options.WindowWidth.HasValue; InstanceWindowWidth = options.WindowWidth ?? settings.GameWindowWidth ?? 854; InstanceWindowHeight = options.WindowHeight ?? settings.GameWindowHeight ?? 480;
        InstanceFullscreen = options.Fullscreen ?? settings.GameFullscreen; InstanceCustomInfo = options.CustomInfo ?? settings.CustomInfo ?? ""; InstanceGameArguments = options.GameArguments;
    }
    private GameLaunchOptions? BuildInstanceLaunchOptions() => UseGlobalLaunchOptions ? null : (SelectedInstance?.LaunchOptions ?? Owner.Snapshot.Settings.LaunchOptions ?? new()) with
    { InheritGlobal = false, Priority = InstancePriority, IpPreference = InstanceIpPreference, Visibility = InstanceVisibility, WindowTitle = InstanceWindowTitle.Trim(), JvmArguments = InstanceJvmArguments.Trim(), LockMemory = InstanceLockMemory,
      WindowWidth = InstanceCustomWindow ? (int)InstanceWindowWidth : null, WindowHeight = InstanceCustomWindow ? (int)InstanceWindowHeight : null, Fullscreen = InstanceFullscreen, CustomInfo = InstanceCustomInfo.Trim(), GameArguments = InstanceGameArguments.Trim() };
}
