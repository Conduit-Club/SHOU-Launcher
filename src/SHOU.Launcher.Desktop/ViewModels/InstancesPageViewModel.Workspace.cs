using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record InstanceSectionChoice(string Key, string Title, string Icon)
{
    public string IconPath => Themes.ClubIcons.ForInstanceSection(Key);
    public bool StartsResources => Key == "worlds";
}
public sealed partial class InstancesPageViewModel
{
    private bool _isSelection = true;
    private bool _workspaceReady;
    private string _searchText = "";
    private InstanceSectionChoice _selectedSection = null!;
    private string _editName = "", _instanceJava = "", _feedback = "";
    private decimal _instanceMemory = 2048;
    private bool _useGlobalJava = true, _useGlobalMemory = true, _includeWorlds, _instanceDraftDirty;
    private string? _draftInstanceId;
    private InstanceContentEntry? _selectedContent;
    public IReadOnlyList<InstanceSectionChoice> Sections { get; } = [new("overview", "概览", "◉"), new("settings", "设置", "⚙"),
        new("modify", "修改", "↻"), new("export", "导出", "⇧"), new("worlds", "存档", "▣"), new("screenshots", "截图", "▧"),
        new("mods", "模组", "◇"), new("resourcepacks", "资源包", "▤"), new("shaders", "光影包", "☼"),
        new("schematics", "投影原理图", "▥"), new("servers", "服务器", "▱"), new("relations", "模组关系", "⌘")];
    public ObservableCollection<GameInstance> FilteredInstances { get; } = [];
    public ObservableCollection<InstanceContentEntry> ContentEntries { get; } = [];
    public bool IsSelection { get => _isSelection; private set { if (SetProperty(ref _isSelection, value)) NotifyWorkspace(); } }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) FilterInstances(); } }
    public string InstanceCountSummary => $"全部实例（{FilteredInstances.Count}）";
    public string GameDirectorySummary => Owner.Snapshot.Settings.GameRoot ?? Service.Paths.InstancesDirectory;
    public string ContextTitle => IsSelection ? "实例选择" : "实例设置 · " + (SelectedInstance?.Name ?? "尚未选择实例");
    public InstanceSectionChoice SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetProperty(ref _selectedSection, value)) return;
            Feedback = ""; ContentEntries.Clear(); SelectedContent = null; ClearScreenshotPreviews(); NotifyWorkspace();
            if (IsFileSection && _workspaceReady && SelectedInstance is not null) _ = RefreshContentCommand.ExecuteAsync(null);
        }
    }
    public bool IsOverview => !IsSelection && SelectedSection?.Key == "overview";
    public bool IsInstanceSettings => !IsSelection && SelectedSection?.Key == "settings";
    public bool IsModify => !IsSelection && SelectedSection?.Key == "modify";
    public bool IsExport => !IsSelection && SelectedSection?.Key == "export";
    public bool IsFileSection => !IsSelection && SelectedSection?.Key is "worlds" or "screenshots" or "mods" or "resourcepacks" or "shaders" or "schematics";
    public bool IsOtherSection => !IsSelection && !IsOverview && !IsInstanceSettings && !IsModify && !IsExport && !IsFileSection;
    public bool ModsUnsupported => SelectedSection?.Key == "mods" && SelectedInstance?.Loader == "vanilla";
    public bool IsContentEmpty => ContentEntries.Count == 0;
    public bool CanImportContent => IsFileSection && SelectedSection.Key != "worlds" && !ModsUnsupported && CanEdit && Service is IInstanceWorkspaceService;
    public string SectionTitle => SelectedSection?.Title ?? "实例";
    public string ContentEmptyTitle => ModsUnsupported ? "该实例不可使用模组" : "尚未发现" + SectionTitle;
    public string CapabilityReason => SelectedSection?.Key switch
    {
        "servers" => "服务器列表的 NBT 编辑与单实例连接仍待移植，可直接在游戏内管理；原文件保持。",
        "relations" => "模组依赖关系的解析与图形视图仍待移植。",
        "modify" => "当前可重新校验原版与 Fabric。其他加载器的安装及迁移尚未开放。",
        _ when ModsUnsupported => "此实例尚未安装模组加载器，请先选择支持模组的实例。",
        _ => "列表来自当前实例目录。可打开文件夹，或导入支持的本机文件。"
    };
    public string EditName { get => _editName; set { if (SetProperty(ref _editName, value)) _instanceDraftDirty = true; } }
    public string InstanceJavaExecutable { get => _instanceJava; set { if (SetProperty(ref _instanceJava, value)) _instanceDraftDirty = true; } }
    public decimal InstanceMemoryMiB { get => _instanceMemory; set { if (SetProperty(ref _instanceMemory, value)) _instanceDraftDirty = true; } }
    public bool UseGlobalJava { get => _useGlobalJava; set { if (SetProperty(ref _useGlobalJava, value)) _instanceDraftDirty = true; } }
    public bool UseGlobalMemory { get => _useGlobalMemory; set { if (SetProperty(ref _useGlobalMemory, value)) _instanceDraftDirty = true; } }
    public bool IncludeWorlds { get => _includeWorlds; set => SetProperty(ref _includeWorlds, value); }
    public string Feedback { get => _feedback; private set { SetProperty(ref _feedback, value); OnPropertyChanged(nameof(HasFeedback)); } }
    public bool HasFeedback => Feedback.Length > 0;
    public InstanceContentEntry? SelectedContent { get => _selectedContent; set { if (SetProperty(ref _selectedContent, value)) NotifyWorkspace(); } }
    public RelayCommand ShowInstanceSettingsCommand { get; private set; } = null!;
    public RelayCommand ShowSelectionCommand { get; private set; } = null!;
    public RelayCommand NavigateBackCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveInstanceSettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshContentCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenInstanceDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenContentDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand ToggleModCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportInstanceCommand { get; private set; } = null!;
    private void InitializeWorkspace()
    {
        _selectedSection = Sections[0];
        ShowInstanceSettingsCommand = Command(() => { IsSelection = false; SelectedSection = Sections[0]; Owner.SelectedPage = this; RefreshWorkspaceSnapshot(Owner.Snapshot); }, () => SelectedInstance is not null);
        ShowSelectionCommand = Command(() => { IsSelection = true; Owner.SelectedPage = this; });
        NavigateBackCommand = Command(() => { if (IsSelection) Owner.SelectedPage = Owner.Home; else IsSelection = true; });
        SaveInstanceSettingsCommand = AsyncCommand(SaveInstanceEditAsync, () => CanEdit && SelectedInstance is not null && Service is IInstanceWorkspaceService);
        RefreshContentCommand = AsyncCommand(RefreshContentAsync, () => SelectedInstance is not null && IsFileSection && Owner.CanReadRemote && Service is IInstanceWorkspaceService);
        OpenInstanceDirectoryCommand = AsyncCommand(() => Owner.RunControlAsync("打开实例目录", ct => Owner.ExternalLauncher.OpenDirectoryAsync(SelectedInstance!.GameDirectory, ct)), () => SelectedInstance is not null);
        OpenContentDirectoryCommand = AsyncCommand(OpenContentDirectoryAsync, () => SelectedInstance is not null && IsFileSection);
        ToggleModCommand = AsyncCommand(ToggleModAsync, () => CanEdit && SelectedSection.Key == "mods" && SelectedContent is not null && Service is IInstanceWorkspaceService);
        ExportInstanceCommand = AsyncCommand(ExportAsync, () => CanEdit && SelectedInstance is not null && Service is IInstanceWorkspaceService);
        _workspaceReady = true;
    }
    private void RefreshWorkspaceSnapshot(LauncherSnapshot snapshot)
    {
        if (!_workspaceReady) return;
        FilterInstances();
        if (SelectedInstance is { } selected && (_draftInstanceId != selected.Id || !_instanceDraftDirty))
        {
            _draftInstanceId = selected.Id;
            EditName = selected.Name; InstanceJavaExecutable = selected.JavaExecutable ?? "";
            InstanceMemoryMiB = selected.MemoryMiB ?? snapshot.Settings.MemoryMiB;
            UseGlobalJava = selected.JavaExecutable is null; UseGlobalMemory = selected.MemoryMiB is null;
            _instanceDraftDirty = false;
        }
        NotifyWorkspace();
    }
    private void FilterInstances()
    {
        var query = SearchText.Trim();
        var rows = Instances.Where(i => query.Length == 0 || (i.Name + " " + i.VersionId + " " + i.Loader).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!FilteredInstances.SequenceEqual(rows))
        {
            FilteredInstances.Clear(); foreach (var row in rows) FilteredInstances.Add(row);
            OnPropertyChanged(nameof(SelectedInstance));
        }
        OnPropertyChanged(nameof(InstanceCountSummary));
    }
    private void NotifyWorkspace()
    {
        foreach (var name in new[] { nameof(ContextTitle), nameof(IsOverview), nameof(IsInstanceSettings), nameof(IsModify), nameof(IsExport), nameof(IsFileSection), nameof(IsOtherSection),
                     nameof(SectionTitle), nameof(ContentEmptyTitle), nameof(CapabilityReason), nameof(ModsUnsupported), nameof(IsContentEmpty), nameof(CanImportContent), nameof(IsScreenshots), nameof(IsContentList), nameof(ScreenshotSummary) }) OnPropertyChanged(name);
        if (_workspaceReady) NotifyCommands();
    }
    private async Task SaveInstanceEditAsync()
    {
        if (!UseGlobalMemory && (decimal.Truncate(InstanceMemoryMiB) != InstanceMemoryMiB || InstanceMemoryMiB is < 512 or > 32768))
        { Owner.ShowError("实例内存须为512–32768 MiB的整数。"); return; }
        var edit = new InstanceEdit(EditName.Trim(), UseGlobalJava ? null : InstanceJavaExecutable.Trim(), UseGlobalMemory ? null : (int)InstanceMemoryMiB);
        if (await Owner.RunAsync("保存实例设置", ct => ((IInstanceWorkspaceService)Service).UpdateInstanceAsync(SelectedInstance!.Id, edit, ct)))
        { _instanceDraftDirty = false; RefreshWorkspaceSnapshot(Service.Snapshot); Feedback = "实例设置已保存。"; }
    }
    private async Task RefreshContentAsync()
    {
        var selected = SelectedInstance!; var section = SelectedSection.Key;
        await Owner.RunAsync("读取实例" + SectionTitle, async ct =>
        {
            var rows = await ((IInstanceWorkspaceService)Service).ListInstanceContentAsync(selected.Id, section, ct);
            if (SelectedInstance?.Id != selected.Id || SelectedSection.Key != section) return;
            ContentEntries.Clear(); foreach (var row in rows) ContentEntries.Add(row);
            SelectedContent = null; NotifyWorkspace();
            if (section == "screenshots") await LoadScreenshotPreviewsAsync(selected.Id, rows, ct);
        }, true);
    }
    public async Task ImportContentFileAsync(string path)
    {
        if (!CanImportContent) return;
        var section = SelectedSection.Key;
        if (await Owner.RunAsync("导入实例文件", ct => ((IInstanceWorkspaceService)Service).ImportInstanceContentAsync(SelectedInstance!.Id, section, path, ct)))
        { Feedback = "已导入文件。"; await RefreshContentCommand.ExecuteAsync(null); }
    }
    private async Task ToggleModAsync()
    {
        var selected = SelectedContent!;
        if (await Owner.RunAsync(selected.Enabled ? "禁用模组" : "启用模组", ct => ((IInstanceWorkspaceService)Service).SetInstanceModEnabledAsync(SelectedInstance!.Id, selected.RelativePath, !selected.Enabled, ct)))
            await RefreshContentCommand.ExecuteAsync(null);
    }
    private Task OpenContentDirectoryAsync() => Owner.RunControlAsync("打开内容文件夹", async ct =>
    {
        var folder = InstanceWorkspace.Folder(SelectedInstance!.GameDirectory, SelectedSection.Key);
        Directory.CreateDirectory(folder);
        await Owner.ExternalLauncher.OpenDirectoryAsync(folder, ct);
    });
    private async Task ExportAsync()
    {
        string? exported = null;
        if (await Owner.RunAsync("导出实例", async ct => exported = await ((IInstanceWorkspaceService)Service).ExportInstanceAsync(SelectedInstance!.Id, IncludeWorlds, ct)))
            Feedback = "已导出至 " + exported;
    }
}
