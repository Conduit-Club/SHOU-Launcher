using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private bool refreshingContentRows;
    private string contentSearchText = "", contentFilter = "全部", contentSort = "资源名称";
    private readonly Dictionary<string, InstanceContentRowViewModel> contentRows = new(StringComparer.Ordinal);
    private InstanceContentRowViewModel? selectedContentRow;
    private CancellationTokenSource? modScanCancellation;
    private readonly List<Task> modReads = [];
    private int modCheckGeneration;
    private bool modScanBusy;
    private string modUpdateStatus = "", modDetail = "", modAction = "";
    private InstanceModUpdate? selectedModUpdate;
    private IReadOnlyList<InstanceModUpdate> pendingModUpdates = [];
    private IReadOnlyList<string> pendingModPaths = [];
    public string ContentSearchText { get => contentSearchText; set { if (SetProperty(ref contentSearchText, value ?? "")) NotifyContentFilter(); } }
    public IReadOnlyList<string> ContentSortChoices { get; } = ["资源名称", "文件名", "添加时间", "文件大小", "标签数量"];
    public string ContentSort { get => contentSort; set { if (SetProperty(ref contentSort, value)) NotifyContentFilter(); } }
    public ObservableCollection<ModFilterViewModel> ModFilters { get; } = [];
    public ObservableCollection<InstanceModUpdate> ModUpdates { get; } = [];
    public ObservableCollection<InstanceModTrash> ModTrash { get; } = [];
    public InstanceModTrash? SelectedModTrash { get; set; }
    public bool HasModTrash => ModTrash.Count > 0;
    public InstanceModUpdate? SelectedModUpdate { get => selectedModUpdate; set { SetProperty(ref selectedModUpdate, value); NotifyCommands(); } }
    public bool ConfirmModUpdate { get; set; }
    public bool IsModScanBusy { get => modScanBusy; private set { SetProperty(ref modScanBusy, value); NotifyModActions(); } }
    public string ModUpdateStatus { get => modUpdateStatus; private set { SetProperty(ref modUpdateStatus, value); OnPropertyChanged(nameof(HasModUpdateStatus)); OnPropertyChanged(nameof(ModUpdateSummary)); OnPropertyChanged(nameof(ModUpdateDetails)); OnPropertyChanged(nameof(HasModUpdateDetails)); } }
    public string ModUpdateSummary => ModUpdateStatus.Split('\n', 2)[0];
    public string ModUpdateDetails => ModUpdateStatus.Contains('\n') ? ModUpdateStatus.Split('\n', 2)[1] : "";
    public bool HasModUpdateDetails => ModUpdateDetails.Length > 0;
    public bool HasModUpdateStatus => ModUpdateStatus.Length > 0 && (!IsModsSection || Owner.IsFeatureVisible("mod-updates"));
    public bool HasModUpdates => ModUpdates.Count > 0;
    public string ModDetail { get => modDetail; private set { SetProperty(ref modDetail, value); OnPropertyChanged(nameof(HasModDetail)); } }
    public bool HasModDetail => ModDetail.Length > 0;
    public string ModActionSummary { get => modAction; private set { SetProperty(ref modAction, value); OnPropertyChanged(nameof(HasModAction)); } }
    public bool HasModAction => ModActionSummary.Length > 0;
    public string ModActionCaption => pendingModUpdates.Count > 0 ? "更新并备份" : "移到回收站";
    public bool HasModSelection => Rows().Any(r => r.Selected);
    public string ModSelectionSummary => Localization.UiLocalization.Instance.Format("已选择 {0} 项", Rows().Count(r => r.Selected));
    public bool CanManageMods => CanEdit && !IsSelectedInstanceRunning && IsModsSection && !IsModScanBusy;
    public InstanceContentRowViewModel? SelectedContentRow { get => selectedContentRow; set { if (SetProperty(ref selectedContentRow, value)) SelectedContent = value?.Entry; } }
    public IReadOnlyList<InstanceContentEntry> FilteredContentEntries => FilteredContentRows.Select(r => r.Entry).ToArray();
    private InstanceContentRowViewModel Row(InstanceContentEntry entry)
    {
        if (contentRows.TryGetValue(entry.RelativePath, out var existing)) return existing;
        var row = new InstanceContentRowViewModel(entry, NotifyModActions, RequestContentRowUpdateAsync, ShowContentDetails,
            RevealContentRowAsync, ToggleContentRowAsync, r => { if (CanTrashContent && CurrentContentRow(r)) RequestModTrash([r.Entry.RelativePath]); });
        contentRows.Add(entry.RelativePath, row); return row;
    }
    private InstanceContentRowViewModel[] Rows() => ContentEntries.Select(Row).ToArray();
    public IReadOnlyList<InstanceContentRowViewModel> FilteredContentRows
    {
        get
        {
            var rows = Rows().Where(r => (ContentSearchText.Trim().Length == 0 || (r.Title + " " + r.Entry.Name + " " + r.Entry.VersionLabel + " " + r.Description + " " + r.SearchTags).Contains(ContentSearchText.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                (contentFilter switch { "可更新" => r.HasUpdate, "启用" => r.Entry.Enabled, "禁用" => !r.Entry.Enabled, "错误" => r.HasError, "重复" => r.Duplicate, _ => true }));
            var sorted = (ContentSort switch { "文件名" => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase), "添加时间" => rows.OrderByDescending(r => r.Entry.Created ?? r.Entry.Modified), "文件大小" => rows.OrderByDescending(r => r.Entry.Bytes), "标签数量" => rows.OrderByDescending(r => r.Project?.Categories?.Count ?? 0), _ => rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase) });
            return sorted.OrderByDescending(r => r.IsDirectory).ToArray();
        }
    }
    private void NotifyContentFilter()
    {
        OnPropertyChanged(nameof(FilteredContentEntries)); OnPropertyChanged(nameof(FilteredContentRows)); OnPropertyChanged(nameof(IsContentEmpty)); OnPropertyChanged(nameof(ContentEmptyTitle));
        var rows = Rows().Where(r => ContentSearchText.Trim().Length == 0 || (r.Title + " " + r.Name + " " + r.Version + " " + r.Description + " " + r.SearchTags).Contains(ContentSearchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var filter in ModFilters)
        {
            var count = filter.Key switch { "可更新" => rows.Count(r => r.HasUpdate), "启用" => rows.Count(r => r.Entry.Enabled), "禁用" => rows.Count(r => !r.Entry.Enabled), "错误" => rows.Count(r => r.HasError), "重复" => rows.Count(r => r.Duplicate), _ => rows.Length };
            filter.Title = Localization.UiLocalization.Instance.Format("{0}（{1}）", Localization.UiLocalization.Instance.Text(filter.Key), count); filter.Active = contentFilter == filter.Key;
            filter.Shown = filter.Active || filter.Key == "全部" || (filter.Key switch { "启用" => IsModsSection && count > 0 && count < rows.Length, "禁用" or "可更新" or "错误" or "重复" => count > 0, _ => false });
        }
    }
    public RelayCommand DownloadInstanceResourcesCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckModUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyModUpdateCommand { get; private set; } = null!;
    public RelayCommand CancelModScanCommand { get; private set; } = null!;
    public RelayCommand SelectAllModsCommand { get; private set; } = null!;
    public RelayCommand ClearModSelectionCommand { get; private set; } = null!;
    public AsyncRelayCommand UpdateSelectedModsCommand { get; private set; } = null!;
    public AsyncRelayCommand EnableSelectedModsCommand { get; private set; } = null!;
    public AsyncRelayCommand DisableSelectedModsCommand { get; private set; } = null!;
    public RelayCommand TrashSelectedModsCommand { get; private set; } = null!;
    public AsyncRelayCommand ConfirmModActionCommand { get; private set; } = null!;
    public RelayCommand CancelModActionCommand { get; private set; } = null!;
    public RelayCommand CloseModDetailsCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenModChangelogCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshModTrashCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreModsCommand { get; private set; } = null!;
    public RelayCommand DiagnoseModsCommand { get; private set; } = null!;
    private void InitializeModManagement()
    {
        foreach (var key in new[] { "全部", "启用", "禁用", "可更新", "错误", "重复" }) ModFilters.Add(new(key, k => { contentFilter = k; NotifyContentFilter(); }));
        ContentEntries.CollectionChanged += (_, _) => { if (!refreshingContentRows) NotifyContentFilter(); };
        DownloadInstanceResourcesCommand = Command(() => { Owner.Downloads.OpenInstanceResourceBrowser(SelectedInstance!, SelectedSection.Key); Owner.SelectedPage = Owner.Downloads; }, () => CanEdit && SelectedInstance is not null && SelectedSection.Key is "mods" or "resourcepacks" or "shaders");
        CheckModUpdatesCommand = AsyncCommand(() => CheckModUpdatesAsync(false), () => CanEdit && !IsModScanBusy && SelectedInstance is not null && (IsModsSection && Service is IInstanceModUpdateService || IsResourceUpdateSection && Service is IInstanceResourceUpdateService));
        CancelModScanCommand = Command(() => { StopModScan(); ModUpdateStatus = Localization.UiLocalization.Instance.Text(IsModsSection ? "检查已取消，未修改模组文件。" : "检查已取消，未修改资源文件。"); }, () => IsModScanBusy);
        ApplyModUpdateCommand = AsyncCommand(() => RequestContentUpdatesAsync(SelectedModUpdate is { } u ? [u] : []), () => CanUpdateContent && SelectedModUpdate is not null);
        SelectAllModsCommand = Command(() => { var visible = FilteredContentRows.Where(r => r.IsFile).ToArray(); var value = visible.Any(r => !r.Selected); foreach (var row in visible) row.Selected = value; }, () => IsContentList);
        ClearModSelectionCommand = Command(() => { foreach (var row in Rows()) row.Selected = false; }, () => HasModSelection);
        UpdateSelectedModsCommand = AsyncCommand(() => RequestContentUpdatesAsync(Rows().Where(r => r.Selected && r.Update is not null).Select(r => r.Update!).ToArray()), () => CanUpdateContent && Rows().Any(r => r.Selected && r.HasUpdate));
        EnableSelectedModsCommand = AsyncCommand(() => ToggleSelectedModsAsync(true), () => CanManageMods && HasModSelection && Service is IInstanceModManagementService);
        DisableSelectedModsCommand = AsyncCommand(() => ToggleSelectedModsAsync(false), () => CanManageMods && HasModSelection && Service is IInstanceModManagementService);
        TrashSelectedModsCommand = Command(() => RequestModTrash(Rows().Where(r => r.Selected).Select(r => r.Entry.RelativePath).ToArray()), () => CanTrashContent && HasModSelection);
        ConfirmModActionCommand = AsyncCommand(ConfirmModActionAsync, () => HasModAction && (pendingModUpdates.Count > 0 ? CanManageMods : CanTrashContent));
        CancelModActionCommand = Command(() => { ModActionSummary = ""; pendingModUpdates = []; pendingModPaths = []; }, () => HasModAction);
        CloseModDetailsCommand = Command(() => { detailRow = null; ModDetail = ""; OnPropertyChanged(nameof(CanOpenContentProject)); });
        OpenContentProjectCommand = AsyncCommand(OpenContentProjectAsync, () => CanOpenContentProject);
        CloseContentTrashCommand = Command(() => { ModTrash.Clear(); SelectedModTrash = null; OnPropertyChanged(nameof(HasModTrash)); NotifyModActions(); });
        DiagnoseModsCommand = Command(DiagnoseMods, () => IsModsSection);
        OpenModChangelogCommand = AsyncCommand(async () => { if (SelectedContentRow?.Update?.ChangelogUri is { } uri) await Owner.RunControlAsync("查看更新日志", ct => Owner.ExternalLauncher.OpenUriAsync(uri, ct)); }, () => SelectedContentRow?.Update?.ChangelogUri is not null);
        RefreshModTrashCommand = AsyncCommand(ReadModTrashAsync, () => CanReadContentTrash);
        RestoreModsCommand = AsyncCommand(RestoreContentTrashAsync, () => CanTrashContent && HasModTrash);
    }
    private void NotifyModActions()
    {
        OnPropertyChanged(nameof(HasModSelection)); OnPropertyChanged(nameof(HasSelectedMods)); OnPropertyChanged(nameof(ResourceListBottomMargin)); OnPropertyChanged(nameof(ModSelectionSummary)); OnPropertyChanged(nameof(CanManageMods)); OnPropertyChanged(nameof(CanUpdateContent)); OnPropertyChanged(nameof(CanTrashMods)); OnPropertyChanged(nameof(CanTrashContent)); OnPropertyChanged(nameof(CanReadContentTrash)); OnPropertyChanged(nameof(ContentTrashTitle)); OnPropertyChanged(nameof(CanOpenContentProject)); OnPropertyChanged(nameof(CheckContentUpdatesCaption)); if (_workspaceReady && !_workspaceDisposed) NotifyCommands();
    }
    internal void StopModScan() { ++modCheckGeneration; modScanCancellation?.Cancel(); IsModScanBusy = false; }
    private async Task StopModScanAsync()
    {
        StopModScan();
        StopModRelations();
        // In particular on Windows, allow all cancelled readers to close their JAR handles before a move.
        var readers = modReads.Append(CheckModUpdatesCommand?.ExecutionTask ?? Task.CompletedTask).Where(t => !t.IsCompleted).ToArray();
        if (readers.Length > 0) await Task.WhenAll(readers);
        var relationTasks = relationReads.Append(RefreshModRelationsCommand?.ExecutionTask ?? Task.CompletedTask).Where(t => !t.IsCompleted).ToArray();
        if (relationTasks.Length > 0) await Task.WhenAll(relationTasks);
        relationReads.RemoveAll(t => t.IsCompleted);
        modReads.RemoveAll(t => t.IsCompleted);
    }
    private void ClearModUpdates()
    {
        ClearModRelations();
        StopModScan(); ModUpdates.Clear(); SelectedModUpdate = null; ModUpdateStatus = ""; contentRows.Clear(); contentFilter = "全部";
        ModDetail = ""; detailRow = null; ModTrash.Clear(); SelectedModTrash = null; OnPropertyChanged(nameof(HasModTrash)); ModActionSummary = ""; pendingModUpdates = []; pendingModPaths = []; OnPropertyChanged(nameof(HasModUpdates)); NotifyModActions();
    }
    internal void ModWorkspaceNavigationChanged()
    {
        ExportNavigationChanged();
        ServerStatusNavigationChanged();
        if (!ReferenceEquals(Owner.SelectedPage, this)) { StopModScan(); StopModRelations(); }
        else if (IsContentList) { _pendingContentRefresh = true; ScheduleContentRefresh(); }
        else if (IsRelationsSection) ScheduleModRelations();
    }
    private void StartAutoModScan()
    {
        if (!_workspaceDisposed && ReferenceEquals(Owner.SelectedPage, this) && IsResourceUpdateSection && Service is IInstanceResourceUpdateService && !IsModScanBusy)
        { modReads.RemoveAll(t => t.IsCompleted); modReads.Add(CheckModUpdatesAsync(true)); return; }
        if (!_workspaceDisposed && ReferenceEquals(Owner.SelectedPage, this) && IsResourceBrowserSection && !IsModsSection && Service is IInstanceContentMetadataService && !IsModScanBusy)
        { modReads.RemoveAll(t => t.IsCompleted); modReads.Add(ReadResourceMetadataAsync()); return; }
        if (_workspaceDisposed || !ReferenceEquals(Owner.SelectedPage, this) || !IsModsSection || !Owner.IsFeatureVisible("mod-updates") || Service is not IInstanceModUpdateService || IsModScanBusy) return;
        modReads.RemoveAll(t => t.IsCompleted);
        modReads.Add(CheckModUpdatesAsync(true));
    }
    private async Task CheckModUpdatesAsync(bool automatic)
    {
        StopModScan(); var instance = SelectedInstance!; var section = SelectedSection.Key; var resource = section != "mods"; var generation = ++modCheckGeneration; var source = new CancellationTokenSource(); modScanCancellation = source;
        if (resource) { ModUpdates.Clear(); SelectedModUpdate = null; foreach (var row in Rows()) row.Update = null; NotifyContentFilter(); }
        IsModScanBusy = true; ModUpdateStatus = Localization.UiLocalization.Instance.Text(resource ? "正在核对已安装资源及其官方更新，可随时取消。" : "正在核对已安装模组及其官方更新，可随时取消。");
        bool Current() => !_workspaceDisposed && generation == modCheckGeneration && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && SelectedSection.Key == section && ReferenceEquals(Owner.SelectedPage, this);
        var progress = new Progress<PackInstallProgress>(p => { if (Current()) ModUpdateStatus = Localization.UiLocalization.Instance.Format(resource ? "已检查 {0}/{1} 项资源" : "已检查 {0}/{1} 个模组", p.CompletedFiles, p.TotalFiles) + (p.CurrentFile is { } name ? " · " + UiPrivacy.Redact(name) : "") + "；未修改文件。"; });
        try
        {
            InstanceModUpdateCheck? result = null;
            async Task Read(CancellationToken ct)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, source.Token);
                result = resource ? await ((IInstanceResourceUpdateService)Service).CheckInstanceResourceUpdatesAsync(instance.Id, section, !automatic, progress, linked.Token)
                    : !automatic && Service is IInstanceModRefreshService refresh ? await refresh.RefreshInstanceModUpdatesAsync(instance.Id, progress, linked.Token) : Service is IInstanceModUpdateProgressService tracked ? await tracked.CheckInstanceModUpdatesWithProgressAsync(instance.Id, progress, linked.Token) : await ((IInstanceModUpdateService)Service).CheckInstanceModUpdatesAsync(instance.Id, linked.Token);
            }
            if (automatic) await Read(source.Token);
            else if (!await (resource ? Owner.RunInlineAsync("检查资源更新", Read) : Owner.RunAsync("检查模组更新", Read)))
            { if (Current()) ModUpdateStatus = Localization.UiLocalization.Instance.Text(resource ? "检查已取消或失败，未修改资源文件。" : "检查已取消或失败，未修改模组文件。"); return; }
            if (!Current() || result is null) return;
            ModUpdates.Clear(); foreach (var update in result.Updates) ModUpdates.Add(update); SelectedModUpdate = ModUpdates.FirstOrDefault();
            foreach (var row in Rows()) row.Update = ModUpdates.FirstOrDefault(u => u.RelativePath == row.Entry.RelativePath);
            foreach (var online in result.OnlineInfo ?? []) if (contentRows.TryGetValue(online.RelativePath, out var row)) { row.Project = online.Project; if (resource) { row.ResourceRequirements = online.Requirements ?? []; row.Entry = row.Entry with { ModVersion = online.Version }; } else row.Refresh(); }
            MarkDuplicateMods(); NotifyContentFilter(); OnPropertyChanged(nameof(HasModUpdates));
            ModUpdateStatus = resource ? Localization.UiLocalization.Instance.Format("已检查 {0} 项资源，发现 {1} 项同游戏版本的正式版更新。", result.CheckedFiles, result.Updates.Count)
                : $"已检查 {result.CheckedFiles} 个模组，发现 {result.Updates.Count} 项同游戏版本/加载器的正式版更新。";
            if (result.Notices.Count > 0) ModUpdateStatus += Localization.UiLocalization.Instance.Format(" {0} 项未能核实，未计为最新版。", result.Notices.Count) + "\n" + string.Join("\n", result.Notices.Take(5));
            IsModScanBusy = false;
            if (Service is ILauncherCatalogService icons)
                foreach (var row in Rows().Where(r => !r.Entry.HasIcon && r.Project?.IconUrl is not null))
                {
                    if (!Current()) break;
                    try { var bytes = await icons.GetContentIconAsync(row.Project!.IconUrl!, source.Token); if (Current()) row.Entry = row.Entry with { IconPng = bytes }; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or AggregateException) { }
                }
        }
        catch (OperationCanceledException) { if (Current()) ModUpdateStatus = Localization.UiLocalization.Instance.Text(IsModsSection ? "检查已取消，未修改模组文件。" : "检查已取消，未修改资源文件。"); }
        catch (Exception ex) { if (Current()) ModUpdateStatus = Localization.UiLocalization.Instance.Text(resource ? "暂未能核实资源更新：" : "暂未能核实模组更新：") + UiPrivacy.Redact(ex.Message); }
        finally { if (generation == modCheckGeneration) { ++modCheckGeneration; IsModScanBusy = false; } if (ReferenceEquals(modScanCancellation, source)) modScanCancellation = null; source.Dispose(); }
    }
    private void MarkDuplicateMods()
    {
        foreach (var row in Rows()) row.Duplicate = false;
        foreach (var group in Rows().Where(r => r.Entry.ModId is not null).GroupBy(r => r.Entry.ModId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) foreach (var row in group) row.Duplicate = true;
        foreach (var group in Rows().Where(r => r.Project is not null).GroupBy(r => r.Project!.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)) foreach (var row in group) row.Duplicate = true;
    }
    private void DiagnoseMods()
    {
        if (Service is IInstanceModRelationsService) { SelectedSection = Sections.Single(s => s.Key == "relations"); return; }
        MarkDuplicateMods(); NotifyContentFilter();
        var rows = Rows(); var ids = rows.Where(r => r.Entry.Enabled && r.Entry.ModId is not null).Select(r => r.Entry.ModId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(["minecraft", "java", "forge", "neoforge", "fabricloader", "quilt_loader", "liteloader"]);
        var lines = new List<string> { $"共 {rows.Length} 个模组；{rows.Count(r => r.HasError)} 项归档错误，{rows.Count(r => r.Duplicate)} 项重复项目。" };
        foreach (var row in rows)
        {
            if (row.HasError) lines.Add(row.Name + "：" + row.Entry.MetadataError);
            foreach (var dependency in row.Entry.Dependencies ?? []) if (!ids.Contains(dependency)) lines.Add(row.Name + "：顶层列表未找到依赖 " + dependency + "（可能由内嵌模组提供）。");
            if (row.Entry.EmbeddedMods is { Count: > 0 } embedded) lines.Add(row.Name + " 内嵌：" + string.Join(", ", embedded));
        }
        lines.Add("此检查核对归档、项目重复和声明的依赖 ID；版本范围与内嵌依赖级联仍需核实。"); ModDetail = string.Join("\n", lines);
    }
    public string ExportContentInformation(bool csv)
    {
        static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        IReadOnlyList<InstanceContentRowViewModel> rows = HasModSelection ? Rows().Where(r => r.Selected).ToArray() : FilteredContentRows;
        return csv ? "Name,Version,File,State\n" + string.Join("\n", rows.Select(r => string.Join(",", new[] { r.Title, r.Entry.VersionLabel, r.Name, r.StateText }.Select(Quote))))
            : string.Join("\n\n", rows.Select(r => r.Detail));
    }
    private void RequestModUpdate(IReadOnlyList<InstanceModUpdate> updates)
    {
        pendingModUpdates = updates.Where(u => u.InstanceId == SelectedInstance?.Id).ToArray(); if (pendingModUpdates.Count == 0) return;
        ModActionSummary = Localization.UiLocalization.Instance.Text("更新所选模组并保留原文件备份。整合包定制版本可能不适合单独更新。") + "\n" + string.Join("\n", pendingModUpdates.Select(u => u.Name + "  " + u.CurrentVersion + " → " + u.NewVersion));
        OnPropertyChanged(nameof(ModActionCaption)); NotifyModActions();
    }
    private async Task ToggleSelectedModsAsync(bool enabled)
    {
        var instance = SelectedInstance!; var paths = Rows().Where(r => r.Selected).Select(r => r.Entry.RelativePath).ToArray();
        if (await Owner.RunAsync(enabled ? "启用模组" : "禁用模组", async ct => { await StopModScanAsync(); await ((IInstanceModManagementService)Service).SetInstanceModsEnabledAsync(instance.Id, paths, enabled, ct); })) await RefreshContentCommand.ExecuteAsync(null);
    }
    private async Task ConfirmModActionAsync()
    {
        if (!HasModAction || !(pendingModUpdates.Count > 0 ? CanManageMods : CanTrashContent) || SelectedInstance is null) return;
        var instance = SelectedInstance!; var kind = SelectedSection.Key; var updates = pendingModUpdates.ToArray(); var paths = pendingModPaths.ToArray();
        ModActionSummary = ""; pendingModUpdates = []; pendingModPaths = []; StopModScan();
        if (updates.Length == 0)
        {
            if (await Owner.RunAsync(kind == "mods" ? "回收模组" : "回收资源", async ct =>
            {
                await StopModScanAsync();
                if (kind == "mods") await ((IInstanceModManagementService)Service).TrashInstanceModsAsync(instance.Id, paths, ct);
                else await ((IInstanceContentTrashService)Service).TrashInstanceContentAsync(instance.Id, kind, paths, ct);
            }) && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && SelectedSection.Key == kind && ReferenceEquals(Owner.SelectedPage, this))
            { await RefreshContentCommand.ExecuteAsync(null); await ReadModTrashAsync(); }
            return;
        }
        var completed = 0; var failures = new List<string>();
        await Owner.RunAsync("更新模组并备份", async ct =>
        {
            await StopModScanAsync();
            foreach (var update in updates)
            {
                ct.ThrowIfCancellationRequested(); if (SelectedInstance?.Id != instance.Id) break;
                try { await ((IInstanceModUpdateService)Service).ApplyInstanceModUpdateAsync(update.Token, ct); completed++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or AggregateException) { failures.Add(update.Name + "：" + UiPrivacy.Redact(ex.Message)); }
            }
        });
        Feedback = $"已更新 {completed}/{updates.Length} 项模组，原文件已备份。" + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : "");
        if (SelectedInstance?.Id == instance.Id && IsModsSection) await RefreshContentCommand.ExecuteAsync(null);
    }
    private async Task ReadModTrashAsync()
    {
        if (SelectedInstance is not { } instance || !CanReadContentTrash) return;
        var kind = SelectedSection.Key; IReadOnlyList<InstanceModTrash>? rows = null;
        if (!await Owner.RunInlineAsync(kind == "mods" ? "读取模组回收站" : "读取资源回收站", async ct => rows = kind == "mods"
            ? await ((IInstanceModManagementService)Service).GetInstanceModTrashAsync(instance.Id, ct)
            : await ((IInstanceContentTrashService)Service).GetInstanceContentTrashAsync(instance.Id, kind, ct)) || rows is null || SelectedInstance?.Id != instance.Id || SelectedInstance.GameDirectory != instance.GameDirectory || SelectedSection.Key != kind || !ReferenceEquals(Owner.SelectedPage, this)) return;
        ModTrash.Clear(); foreach (var row in rows) ModTrash.Add(row); SelectedModTrash = ModTrash.FirstOrDefault(); OnPropertyChanged(nameof(SelectedModTrash)); OnPropertyChanged(nameof(HasModTrash)); NotifyModActions();
    }
}

public sealed partial class DownloadsPageViewModel
{
    public void OpenInstanceResourceBrowser(GameInstance instance, string section)
    {
        var target = ContentInstallTargets.FirstOrDefault(t => t.Instance.Id == instance.Id && t.Instance.GameDirectory == instance.GameDirectory) ?? throw new InvalidOperationException("目标实例已改变，请重新选择。");
        SelectedInstallTarget = target; _isConfiguration = false; _isResourceDetail = false; _isPackageDetail = false;
        SelectCategory(section switch { "mods" => "mod", "resourcepacks" => "resourcepack", "shaders" => "shader", _ => throw new ArgumentException("此类别没有资源目录。") });
        GameVersionFilter = InstanceIdentity.GameVersion(instance);
        LoaderFilter = section == "mods" ? LoaderFilters.FirstOrDefault(l => l.Equals(instance.Loader, StringComparison.OrdinalIgnoreCase)) ?? "任意" : "任意";
    }
}
