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
public sealed partial class PackOptionalFileViewModel(string path, string? name = null, string? detail = null) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = name ?? path;
    public string Detail { get; } = detail ?? "";
    public bool HasDetail => Detail.Length > 0;
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
    private string _localPackPath = "", _packName = "", _packStatus = "";
    private ModrinthPackInspection? _packInspection;
    private LocalPackInspection? _localPackInspection;
    private IReadOnlyList<string> _packImportDetails = [];
    private int _packRevision;
    private bool _packPreflightSucceeded;
    private bool _localPackSelectionDirty;
    public bool PreparePackAfterImport { get; set; }
    public ObservableCollection<ContentTargetChoice> ContentInstallTargets { get; } = [];
    public ContentTargetChoice? SelectedInstallTarget
    {
        get => _installTarget;
        set { if (value is null && _updatingTargets) return; if (SetProperty(ref _installTarget, value)) { WorldInstallTargetChanged(); NotifyCommands(); } }
    }
    public bool ShowResourceInstall => IsResourceDetail && SelectedProject?.Kind is "mod" or "resourcepack" or "shader";
    public bool ShowPackImport => IsSearchPage && SelectedCategory.Id == "modpack";
    public bool ShowLocalPackPanel => ShowPackImport && LocalPackPath.Length > 0;
    public bool PackServiceAvailable => _packs is not null || _localPacks is not null;
    public string ResourceInstallStatus { get => _resourceInstallStatus; private set => SetProperty(ref _resourceInstallStatus, value); }
    public string PackStatus { get => _packStatus; private set => SetProperty(ref _packStatus, value); }
    public string PackInstanceName { get => _packName; set { if (SetProperty(ref _packName, value)) ImportLocalPackCommand.NotifyCanExecuteChanged(); } }
    public string LocalPackPath => _localPackPath;
    public ModrinthPackInspection? PackInspection => _packInspection;
    public LocalPackInspection? DetectedLocalPack => _localPackInspection;
    public bool HasPackInspection => _localPackInspection is not null || _packInspection is not null;
    public string PackDetails => string.Join(Environment.NewLine,
        (_packPreflightSucceeded ? _localPackInspection?.Warnings ?? _packInspection?.Warnings ?? [] : []).Concat(_packImportDetails).Distinct(StringComparer.Ordinal));
    public bool HasPackDetails => PackDetails.Length > 0;
    private bool CanImportLocalPack => _packPreflightSucceeded && (_localPackSelectionDirty || (_localPackInspection?.CanImport ?? _packInspection?.CanImport ?? false));
    public string LocalPackImportAction => _localPackSelectionDirty ? "检查选择并导入" : "导入到独立新实例";
    public ObservableCollection<PackManualFileRowViewModel> LocalPackManualFiles { get; } = [];
    public bool HasLocalPackManualFiles => LocalPackManualFiles.Count > 0;
    public ObservableCollection<PackOptionalFileViewModel> OptionalPackFiles { get; } = [];
    public bool HasOptionalPackFiles => OptionalPackFiles.Count > 0;
    public AsyncRelayCommand ImportLocalPackCommand { get; private set; } = null!;
    public AsyncRelayCommand ReadCachedPackCommand { get; private set; } = null!;
    public AsyncRelayCommand RecheckLocalPackCommand { get; private set; } = null!;
    private void InitializeContentInstallation(IContentInstallationService? installation, IModrinthPackService? packs, ILocalPackService? localPacks)
    {
        _contentInstall = installation ?? Service as IContentInstallationService; _packs = packs ?? Service as IModrinthPackService;
        _localPacks = localPacks ?? Service as ILocalPackService;
        InitializeWorldContentInstallation();
        if (_contentInstall is null) ResourceInstallStatus = "当前后端未提供实例资源安装能力；文件仍可明确下载到缓存。";
        ImportLocalPackCommand = AsyncCommand(ImportLocalPackAsync, () => CanEdit && PackServiceAvailable && CanImportLocalPack && !string.IsNullOrWhiteSpace(PackInstanceName) && PackInstanceName.Length <= 80);
        ReadCachedPackCommand = AsyncCommand(() => ReadLocalPackAsync(LastDownloadPath), () => CanEdit && PackServiceAvailable && HasDownloadedFile &&
            (LastDownloadPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || LastDownloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
        RecheckLocalPackCommand = AsyncCommand(() => ReadLocalPackCoreAsync(_localPackPath, preserveDraft: true),
            () => CanEdit && !_disposed && PackServiceAvailable && _localPackPath.Length > 0);
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
        var previousTarget = _installTarget;
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
        OnPropertyChanged(nameof(SelectedInstallTarget));
        if (previousTarget?.Instance.Id != _installTarget?.Instance.Id || previousTarget?.Instance.GameDirectory != _installTarget?.Instance.GameDirectory) WorldInstallTargetChanged();
        else RefreshWorldTargets();
        NotifyCommands();
    }
    internal bool CanInstallResource(ContentRelease? release) => ShowWorldContentInstall ? CanPreviewWorldResource(release) : CanEdit && _contentInstall is not null && ShowResourceInstall &&
        release is not null && _installTarget is { } target && ContentInstallPlanner.Compatible(release, SelectedProject!.Kind, target.GameVersion, target.Instance.Loader);
    internal async Task InstallResourceAsync(ContentRelease release, ContentFile file)
    {
        if (ShowWorldContentInstall) { await PreviewWorldResourceAsync(release, file); return; }
        if (!CanInstallResource(release) || SelectedProject is not { } project || SelectedInstallTarget is not { } target) return;
        var request = new ContentInstallRequest(target.Instance.Id, project.Id, release.Id, file.FileName, project.Kind);
        var revision = _requestRevision; ContentInstallResult? installed = null;
        ResourceInstallStatus = "正在预检required依赖与冲突，随后校验并装入所选实例…";
        var success = await Owner.RunAsync("装入实例资源", async ct => installed = await _contentInstall!.InstallContentAsync(request, ct));
        if (revision != _requestRevision || SelectedProject?.Id != project.Id) return;
        ResourceInstallStatus = success ? $"已装入 {target.Instance.Name}：{installed!.Files.Count} 个资源文件，新增 {installed.AddedFiles} 个。安装清单已保存。" +
            (installed.Warnings.Count > 0 ? " " + string.Join(" ", installed.Warnings) : "") : Owner.ErrorMessage ?? "安装已取消，已有不同文件未被覆盖。";
    }
    public Task ReadLocalPackAsync(string path) => ReadLocalPackCoreAsync(path, preserveDraft: false);
    private async Task ReadLocalPackCoreAsync(string path, bool preserveDraft, string? expectedSha256 = null)
    {
        if (!PackServiceAvailable || !CanEdit || _disposed) return;
        var name = PackInstanceName; var previousLocal = preserveDraft ? _localPackInspection : null;
        var previousMr = preserveDraft ? _packInspection : null;
        var selected = OptionalPackFiles.Where(f => f.Selected).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var previousRows = preserveDraft ? OptionalPackFiles.ToArray() : [];
        var revision = ++_packRevision; _localPackPath = path; _packInspection = null; _localPackInspection = null;
        _packPreflightSucceeded = false; _localPackSelectionDirty = false; _packImportDetails = []; OptionalPackFiles.Clear(); LocalPackManualFiles.Clear();
        PackStatus = preserveDraft ? "正在重新预检整合包与补充文件…" : "正在读取本机整合包…";
        NotifyLocalPackInspection();
        ModrinthPackInspection? inspected = null;
        LocalPackInspection? local = null;
        var success = await Owner.RunAsync("读取本机整合包", async ct =>
        {
            if (preserveDraft && _localPacks is ILocalPackSelectionService choices) { local = await choices.InspectLocalPackSelectionAsync(path, selected.ToArray(), cancellationToken: ct,
                expectedArchiveSha256: expectedSha256 ?? previousLocal?.ArchiveSha256 ?? previousMr?.ArchiveSha256); inspected = local.Modrinth; }
            else if (_localPacks is not null) { local = await _localPacks.InspectLocalPackAsync(path, ct); inspected = local.Modrinth; }
            else inspected = await _packs!.InspectModrinthPackAsync(path, ct);
            ct.ThrowIfCancellationRequested();
        });
        if (revision != _packRevision || _disposed) return;
        if (success)
        {
            var hash = local?.ArchiveSha256 ?? inspected!.ArchiveSha256;
            if (expectedSha256 is not null && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                PackStatus = "整合包在补充文件后已改变，请重新选择并预检，再确认安装。";
                NotifyLocalPackInspection(); return;
            }
            _packPreflightSucceeded = true; _packInspection = inspected; _localPackInspection = local;
            PackInstanceName = preserveDraft ? name : local?.Name ?? inspected!.Name;
            var previousHash = previousLocal?.ArchiveSha256 ?? previousMr?.ArchiveSha256;
            if (local?.Format == LocalPackFormat.CurseForge)
                foreach (var file in local.OptionalFiles ?? []) AddLocalOptionalRow(new(file.Key, file.Name, file.Notice), preserveDraft && hash == previousHash && selected.Contains(file.Key));
            else foreach (var file in inspected?.Files.Where(f => f.Client == "optional") ?? [])
                AddLocalOptionalRow(new(file.Path), preserveDraft && hash == previousHash && selected.Contains(file.Path));
            if (preserveDraft && previousHash is not null && hash != previousHash)
                _packImportDetails = ["整合包文件已改变，本次已按新文件重新预检，请核对内容后安装。"];
            PackStatus = local is not null
                ? local.CanImport ? $"{local.Format switch { LocalPackFormat.CurseForge => "CurseForge ZIP", LocalPackFormat.ShouExport => "SHOU ZIP", _ => "Modrinth .mrpack" }} · Minecraft {local.GameVersion} · {(local.Loader + " " + local.LoaderVersion).Trim()}。"
                    : local.CapabilityReason ?? "此整合包暂不支持导入。"
                : inspected!.CanImport ? $"{inspected.GameVersion} · {inspected.Loader} · {inspected.Files.Count} 项清单文件 · {inspected.Overrides.Count} 项客户端覆盖文件。" + string.Join(" ", inspected.Warnings) : inspected.CapabilityReason ?? "此整合包暂不支持导入。";
        }
        else
        {
            _localPackInspection = previousLocal; _packInspection = previousMr;
            foreach (var file in previousRows) AddLocalOptionalRow(new(file.Path, file.Name, file.Detail), selected.Contains(file.Path));
            PackStatus = Owner.ErrorMessage ?? "整合包预检已取消；可重新预检后继续。";
        }
        PopulateLocalManualFiles(revision);
        NotifyLocalPackInspection();
    }
    private void AddLocalOptionalRow(PackOptionalFileViewModel row, bool selected)
    {
        row.Selected = selected; OptionalPackFiles.Add(row);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(row.Selected) || _disposed || !OptionalPackFiles.Contains(row) ||
                _localPackInspection?.Format != LocalPackFormat.CurseForge || _localPacks is not ILocalPackSelectionService) return;
            ++_packRevision;
            _localPackSelectionDirty = !OptionalPackFiles.Where(f => f.Selected).Select(f => f.Path).ToHashSet(StringComparer.Ordinal)
                .SetEquals(_localPackInspection.SelectedOptionalFiles ?? []);
            LocalPackManualFiles.Clear();
            PackStatus = "可选项目已更改，导入前会重新预检本次选择。";
            NotifyLocalPackInspection();
        };
    }
    private void PopulateLocalManualFiles(int revision)
    {
        LocalPackManualFiles.Clear();
        foreach (var file in _localPackInspection?.ManualFiles ?? [])
            LocalPackManualFiles.Add(new(Owner, file, ProvideLocalManualFileAsync,
                row => !_disposed && revision == _packRevision && LocalPackManualFiles.Contains(row),
                () => _localPacks is ILocalPackManualFileService));
    }
    private async Task ProvideLocalManualFileAsync(PackManualFileRowViewModel row, string path)
    {
        if (!CanEdit || !row.IsCurrent || _localPackInspection is not { } inspection || _localPacks is not ILocalPackManualFileService manual) return;
        var revision = _packRevision; var archive = _localPackPath;
        var selected = OptionalPackFiles.Where(f => f.Selected).Select(f => f.Path).ToArray();
        PackStatus = "正在按官方版本校验补充文件…";
        var success = await Owner.RunAsync("校验补充文件", async ct =>
        {
            if (_localPacks is ILocalPackSelectionService choices)
                await choices.ProvideSelectedLocalPackFileAsync(archive, inspection.ArchiveSha256, selected, row.File.ProjectId, row.File.FileId, path, ct);
            else await manual.ProvideLocalPackFileAsync(archive, inspection.ArchiveSha256, row.File.ProjectId, row.File.FileId, path, ct);
            ct.ThrowIfCancellationRequested();
        });
        if (_disposed || revision != _packRevision) return;
        if (!success)
        {
            _packPreflightSucceeded = false;
            PackStatus = Owner.ErrorMessage ?? "补充文件已取消；待补文件保留，可重试或重新预检。";
            NotifyLocalPackInspection(); return;
        }
        LocalPackManualFiles.Remove(row);
        _localPackInspection = inspection with { ManualFiles = LocalPackManualFiles.Select(f => f.File).ToArray() };
        await ReadLocalPackCoreAsync(archive, preserveDraft: true, expectedSha256: inspection.ArchiveSha256);
    }
    private void NotifyLocalPackInspection()
    {
        foreach (var property in new[] { nameof(LocalPackPath), nameof(ShowLocalPackPanel), nameof(PackInspection), nameof(DetectedLocalPack),
            nameof(HasPackInspection), nameof(HasOptionalPackFiles), nameof(HasLocalPackManualFiles), nameof(LocalPackImportAction), nameof(PackDetails), nameof(HasPackDetails) }) OnPropertyChanged(property);
        foreach (var row in LocalPackManualFiles) row.Refresh();
        NotifyCommands();
    }
    private async Task ImportLocalPackAsync()
    {
        if (!CanEdit || _disposed || !CanImportLocalPack || !PackServiceAvailable) return;
        if (_localPackSelectionDirty)
        {
            await ReadLocalPackCoreAsync(_localPackPath, preserveDraft: true, expectedSha256: _localPackInspection!.ArchiveSha256);
            if (!CanEdit || _disposed || !CanImportLocalPack) return;
        }
        var hash = _localPackInspection?.ArchiveSha256 ?? _packInspection!.ArchiveSha256;
        var selected = OptionalPackFiles.Where(f => f.Selected).Select(f => f.Path).ToArray();
        var request = new ModrinthPackImportRequest(_localPackPath, hash, PackInstanceName, selected, PreparePackAfterImport);
        ModrinthPackImportResult? imported = null; var revision = _packRevision;
        LocalPackImportResult? local = null;
        _packImportDetails = [];
        OnPropertyChanged(nameof(PackDetails)); OnPropertyChanged(nameof(HasPackDetails));
        PackStatus = "正在校验文件并导入独立新实例…";
        var success = await Owner.RunAsync("导入整合包", async ct =>
        {
            if (_localPacks is not null) local = await _localPacks.ImportLocalPackAsync(new(request.ArchivePath, hash, request.InstanceName, selected, request.PrepareAfterImport), ct);
            else imported = await _packs!.ImportModrinthPackAsync(request, ct);
        });
        if (revision != _packRevision || _disposed) return;
        if (success)
        {
            _packImportDetails = local?.Warnings ?? [];
            if (imported is { } result && (result.SkippedServerFiles > 0 || result.SkippedOptionalFiles > 0))
                _packImportDetails = [$"跳过 {result.SkippedServerFiles} 个服务器文件与 {result.SkippedOptionalFiles} 个未选可选文件。"];
            PackStatus = $"已创建 {local?.Instance.Name ?? imported!.Instance.Name}，装入 {local?.InstalledFiles ?? imported!.InstalledFiles} 个文件。首次启动会补全游戏本体。";
        }
        else PackStatus = Owner.ErrorMessage ?? "导入已取消，未登记新实例。";
        OnPropertyChanged(nameof(PackDetails)); OnPropertyChanged(nameof(HasPackDetails));
    }
    private void NotifyContentInstallationPage()
    {
        NotifyWorldContentPage();
        OnPropertyChanged(nameof(ShowResourceInstall)); OnPropertyChanged(nameof(ShowPackImport)); OnPropertyChanged(nameof(ShowLocalPackPanel));
    }
    private void ResetResourceInstallStatus() => ResourceInstallStatus = _contentInstall is null
        ? "当前后端未提供实例资源安装能力；文件仍可明确下载到缓存。"
        : "选择明确的兼容实例后点击文件旁的“装入实例”；required依赖会预检并一同安装。";
}
