using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record ContentTargetChoice(GameInstance Instance, string GameVersion)
{
    public string Label => $"{Instance.Name} · {GameVersion} · {Instance.Loader}";
}
public sealed partial class PackOptionalFileViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    [ObservableProperty] private bool selected;
}

public sealed partial class DownloadsPageViewModel
{
    private IContentInstallationService? _contentInstall;
    private IModrinthPackService? _packs;
    private ILocalPackService? _localPacks;
    private ContentTargetChoice? _installTarget;
    private bool _updatingTargets;
    private string _resourceInstallStatus = "选择明确的兼容实例后点击文件旁的“装入实例”；required依赖会预检并一同安装。";
    private string _localPackPath = "", _packName = "", _packStatus = "将 .zip 或 .mrpack 拖入窗口，或选择本机整合包；确认清单后安装到独立新实例。";
    private ModrinthPackInspection? _packInspection;
    private LocalPackInspection? _localPackInspection;
    private int _packRevision;
    public ObservableCollection<ContentTargetChoice> ContentInstallTargets { get; } = [];
    public ContentTargetChoice? SelectedInstallTarget
    {
        get => _installTarget;
        set { if (value is null && _updatingTargets) return; if (SetProperty(ref _installTarget, value)) NotifyCommands(); }
    }
    public bool ShowResourceInstall => IsResourceDetail && SelectedProject?.Kind is "mod" or "resourcepack" or "shader";
    public bool ShowPackImport => IsSearchPage && SelectedCategory.Id == "modpack" || IsResourceDetail && SelectedProject?.Kind == "modpack";
    public bool PackServiceAvailable => _packs is not null || _localPacks is not null;
    public string ResourceInstallStatus { get => _resourceInstallStatus; private set => SetProperty(ref _resourceInstallStatus, value); }
    public string PackStatus { get => _packStatus; private set => SetProperty(ref _packStatus, value); }
    public string PackInstanceName { get => _packName; set { if (SetProperty(ref _packName, value)) ImportLocalPackCommand.NotifyCanExecuteChanged(); } }
    public string LocalPackPath => _localPackPath;
    public ModrinthPackInspection? PackInspection => _packInspection;
    public LocalPackInspection? DetectedLocalPack => _localPackInspection;
    public bool HasPackInspection => _localPackInspection is not null || _packInspection is not null;
    private bool CanImportLocalPack => _localPackInspection?.CanImport ?? _packInspection?.CanImport ?? false;
    public ObservableCollection<PackOptionalFileViewModel> OptionalPackFiles { get; } = [];
    public bool HasOptionalPackFiles => OptionalPackFiles.Count > 0;
    public AsyncRelayCommand ImportLocalPackCommand { get; private set; } = null!;
    public AsyncRelayCommand ReadCachedPackCommand { get; private set; } = null!;
    private void InitializeContentInstallation(IContentInstallationService? installation, IModrinthPackService? packs, ILocalPackService? localPacks)
    {
        _contentInstall = installation ?? Service as IContentInstallationService; _packs = packs ?? Service as IModrinthPackService;
        _localPacks = localPacks ?? Service as ILocalPackService;
        if (_contentInstall is null) ResourceInstallStatus = "当前后端未提供实例资源安装能力；文件仍可明确下载到缓存。";
        ImportLocalPackCommand = AsyncCommand(ImportLocalPackAsync, () => CanEdit && PackServiceAvailable && CanImportLocalPack && !string.IsNullOrWhiteSpace(PackInstanceName) && PackInstanceName.Length <= 80);
        ReadCachedPackCommand = AsyncCommand(() => ReadLocalPackAsync(LastDownloadPath), () => CanEdit && PackServiceAvailable && HasDownloadedFile &&
            (LastDownloadPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || LastDownloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
        if (!PackServiceAvailable) PackStatus = "当前后端未提供整合包导入能力。";
    }
    private void RefreshContentTargets(LauncherSnapshot snapshot)
    {
        var choices = new List<ContentTargetChoice>();
        foreach (var instance in snapshot.Instances)
        {
            try { choices.Add(new(instance, InstanceIdentity.GameVersion(instance))); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        }
        var selectedId = _installTarget?.Instance.Id ?? snapshot.SelectedInstanceId;
        _updatingTargets = true;
        try
        {
            for (var i = ContentInstallTargets.Count - 1; i >= 0; i--) if (choices.All(c => c.Instance.Id != ContentInstallTargets[i].Instance.Id)) ContentInstallTargets.RemoveAt(i);
            foreach (var choice in choices)
            {
                var current = ContentInstallTargets.FirstOrDefault(c => c.Instance.Id == choice.Instance.Id);
                if (current is null) ContentInstallTargets.Add(choice);
                else if (current != choice) ContentInstallTargets[ContentInstallTargets.IndexOf(current)] = choice;
            }
            _installTarget = ContentInstallTargets.FirstOrDefault(c => c.Instance.Id == selectedId);
        }
        finally { _updatingTargets = false; }
        OnPropertyChanged(nameof(SelectedInstallTarget)); NotifyCommands();
    }
    internal bool CanInstallResource(ContentRelease? release) => CanEdit && _contentInstall is not null && ShowResourceInstall &&
        release is not null && _installTarget is { } target && ContentInstallPlanner.Compatible(release, SelectedProject!.Kind, target.GameVersion, target.Instance.Loader);
    internal async Task InstallResourceAsync(ContentRelease release, ContentFile file)
    {
        if (!CanInstallResource(release) || SelectedProject is not { } project || SelectedInstallTarget is not { } target) return;
        var request = new ContentInstallRequest(target.Instance.Id, project.Id, release.Id, file.FileName, project.Kind);
        var revision = _requestRevision; ContentInstallResult? installed = null;
        ResourceInstallStatus = "正在预检required依赖与冲突，随后校验并装入所选实例…";
        var success = await Owner.RunAsync("装入实例资源", async ct => installed = await _contentInstall!.InstallContentAsync(request, ct));
        if (revision != _requestRevision || SelectedProject?.Id != project.Id) return;
        ResourceInstallStatus = success ? $"已装入 {target.Instance.Name}：{installed!.Files.Count} 个资源文件，新增 {installed.AddedFiles} 个。安装清单已保存。" +
            (installed.Warnings.Count > 0 ? " " + string.Join(" ", installed.Warnings) : "") : Owner.ErrorMessage ?? "安装已取消，已有不同文件未被覆盖。";
    }
    public async Task ReadLocalPackAsync(string path)
    {
        if (!PackServiceAvailable || !CanEdit) return;
        var revision = ++_packRevision; _localPackPath = path; _packInspection = null; _localPackInspection = null; OptionalPackFiles.Clear();
        OnPropertyChanged(nameof(LocalPackPath)); OnPropertyChanged(nameof(PackInspection)); OnPropertyChanged(nameof(HasPackInspection)); OnPropertyChanged(nameof(HasOptionalPackFiles));
        ModrinthPackInspection? inspected = null;
        LocalPackInspection? local = null;
        var success = await Owner.RunAsync("读取本机整合包", async ct =>
        {
            if (_localPacks is not null) { local = await _localPacks.InspectLocalPackAsync(path, ct); inspected = local.Modrinth; }
            else inspected = await _packs!.InspectModrinthPackAsync(path, ct);
        });
        if (revision != _packRevision || _disposed) return;
        if (success)
        {
            _packInspection = inspected; _localPackInspection = local; PackInstanceName = local?.Name ?? inspected!.Name;
            foreach (var file in inspected?.Files.Where(f => f.Client == "optional") ?? []) OptionalPackFiles.Add(new(file.Path));
            PackStatus = local is not null
                ? local.CanImport ? $"{local.Format} · {local.GameVersion} · {local.Loader}。" + string.Join(" ", local.Warnings)
                    : local.CapabilityReason ?? "此整合包暂不支持导入。"
                : inspected!.CanImport ? $"{inspected.GameVersion} · {inspected.Loader} · {inspected.Files.Count} 项清单文件 · {inspected.Overrides.Count} 项客户端覆盖文件。" + string.Join(" ", inspected.Warnings) : inspected.CapabilityReason ?? "此整合包暂不支持导入。";
        }
        else PackStatus = Owner.ErrorMessage ?? "整合包读取已取消。";
        OnPropertyChanged(nameof(PackInspection)); OnPropertyChanged(nameof(DetectedLocalPack)); OnPropertyChanged(nameof(HasPackInspection)); OnPropertyChanged(nameof(HasOptionalPackFiles)); NotifyCommands();
    }
    private async Task ImportLocalPackAsync()
    {
        if (!CanImportLocalPack || !PackServiceAvailable) return;
        var hash = _localPackInspection?.ArchiveSha256 ?? _packInspection!.ArchiveSha256;
        var selected = OptionalPackFiles.Where(f => f.Selected).Select(f => f.Path).ToArray();
        var request = new ModrinthPackImportRequest(_localPackPath, hash, PackInstanceName, selected);
        ModrinthPackImportResult? imported = null; var revision = _packRevision;
        LocalPackImportResult? local = null;
        PackStatus = "正在校验文件并导入独立新实例…";
        var success = await Owner.RunAsync("导入整合包", async ct =>
        {
            if (_localPacks is not null) local = await _localPacks.ImportLocalPackAsync(new(_localPackPath, hash, PackInstanceName, selected), ct);
            else imported = await _packs!.ImportModrinthPackAsync(request, ct);
        });
        if (revision != _packRevision || _disposed) return;
        PackStatus = success ? local is not null
            ? $"已创建 {local.Instance.Name} 并装入 {local.InstalledFiles} 个文件。游戏本体在首次启动时下载。" + string.Join(" ", local.Warnings)
            : $"已创建 {imported!.Instance.Name} 并装入 {imported.InstalledFiles} 个文件；跳过 {imported.SkippedServerFiles} 个服务器文件与 {imported.SkippedOptionalFiles} 个未选可选文件。游戏本体在首次启动时下载。"
            : Owner.ErrorMessage ?? "导入已取消，未登记新实例。";
    }
    private void NotifyContentInstallationPage()
    {
        OnPropertyChanged(nameof(ShowResourceInstall)); OnPropertyChanged(nameof(ShowPackImport));
    }
    private void ResetResourceInstallStatus() => ResourceInstallStatus = _contentInstall is null
        ? "当前后端未提供实例资源安装能力；文件仍可明确下载到缓存。"
        : "选择明确的兼容实例后点击文件旁的“装入实例”；required依赖会预检并一同安装。";
}
