using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceWorldRowViewModel
{
    public InstanceWorldSummary Summary { get; }
    public string Name => Summary.Name;
    public string DirectoryName => Summary.DirectoryName;
    public bool HasError => Summary.Error is not null;
    public string Error => UiPrivacy.Redact(Summary.Error ?? "");
    public string Version => Summary.Version is { Length: > 0 } version ? version + (Summary.DataVersion is { } data ? $"（{data}）" : "") : "未知版本";
    public string LastPlayed => Summary.LastPlayed?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "未知";
    public string Subtitle => HasError ? Error : $"{Version} · 最后游玩：{LastPlayed}";
    public RelayCommand OpenCommand { get; }
    public InstanceWorldRowViewModel(InstanceWorldSummary summary, Action<InstanceWorldRowViewModel> open)
    { Summary = summary; OpenCommand = new(() => open(this), () => !HasError); }
}

public sealed class InstanceDatapackRowViewModel(WorldDatapackFile file) : ObservableObject
{
    public WorldDatapackFile File { get; } = file;
    private WorldDatapackState? details;
    public WorldDatapackState? Details
    {
        get => details;
        internal set { if (SetProperty(ref details, value)) { OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Description)); } }
    }
    public string Name => File.Name;
    public string Description => Details?.Description ?? "";
    public string Subtitle => (Description.Length > 0 ? Description + " · " : "") + (File.IsDirectory ? "文件夹" : $"{File.Size ?? 0:N0} B") + " · " + File.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Status => Details is { } data ? (data.Enabled switch { true => "已启用", false => "已禁用", _ => "未列入世界配置" }) + (data.PackFormat is { } format ? $" · 格式 {format}" : "") + " · " + data.Capability : File.Status;
}

public sealed record InstanceServerDraftViewModel(Guid DraftId, int? ExistingIndex, string Name, string Address);

public sealed partial class InstancesPageViewModel
{
    private enum WorldReadPhase { Unread, Loading, Ready, Error }
    private sealed class WorldPackDraft
    {
        public ObservableCollection<InstanceDatapackRowViewModel> Rows { get; } = [];
        public ObservableCollection<InstanceDatapackRowViewModel> Filtered { get; } = [];
        public ObservableCollection<InstanceDatapackRowViewModel> Selected { get; } = [];
        public WorldReadPhase Phase;
        public string Query = "";
    }
    private sealed class InstanceWorldDraft
    {
        public ObservableCollection<InstanceWorldRowViewModel> Worlds { get; } = [];
        public ObservableCollection<InstanceWorldRowViewModel> FilteredWorlds { get; } = [];
        public ObservableCollection<InstanceServerDraftViewModel> Servers { get; } = [];
        public Dictionary<string, WorldPackDraft> Packs { get; } = new(StringComparer.Ordinal);
        public WorldReadPhase WorldsPhase, ServersPhase;
        public string WorldQuery = "", ServerRevision = "", Feedback = "", Backup = "", WorldRoute = "list";
        public bool ServerDirty, ServerConflict, EditorOpen;
        public string EditorName = "", EditorAddress = "";
        public InstanceServerDraftViewModel? EditingServer, SelectedServer;
        public InstanceWorldRowViewModel? SelectedWorld;
    }
    private readonly Dictionary<string, InstanceWorldDraft> _worldDrafts = new(StringComparer.Ordinal);
    private InstanceWorldDraft _worldDraft = new();
    private readonly WorldPackDraft _emptyPackDraft = new();
    private bool _worldWorkspaceReady;
    private bool _worldUnreadScheduled;
    private string? _worldInstanceKey;
    private string _worldSectionKey = "";
    private WorldPackDraft CurrentWorldPacks => SelectedWorldRow is { } selected && _worldDraft.Packs.TryGetValue(selected.DirectoryName, out var packs) ? packs : _emptyPackDraft;
    public bool IsWorldWorkspaceSection => !IsSelection && _worldSectionKey is "worlds" or "servers";
    public bool IsWorldListPage => IsWorldWorkspaceSection && _worldSectionKey == "worlds" && _worldDraft.WorldRoute == "list";
    public bool IsWorldDetails => IsWorldWorkspaceSection && _worldSectionKey == "worlds" && _worldDraft.WorldRoute != "list" && SelectedWorldRow is not null;
    public bool IsWorldInformationPage => IsWorldDetails && _worldDraft.WorldRoute == "info";
    public bool IsWorldDatapacksPage => IsWorldDetails && _worldDraft.WorldRoute == "datapacks";
    public bool IsInstanceServersPage => IsWorldWorkspaceSection && _worldSectionKey == "servers";
    public bool WorldWorkspaceCanRead => _worldWorkspaceReady && SelectedInstance is not null && Service is IInstanceWorldService
        && Service.Paths.IsIsolated && SelectedInstance.Id == Owner.Snapshot.SelectedInstanceId && Owner.CanReadRemote;
    public bool WorldWorkspaceCanWrite => WorldWorkspaceCanRead && CanEdit && !Owner.HasRunningGame;
    public string WorldWorkspaceCapability => Service is not IInstanceWorldService ? "此服务尚未接入存档与服务器读写。"
        : !Service.Paths.IsIsolated ? "当前并非隔离数据目录，存档和服务器文件的读取与写入已禁用。"
        : SelectedInstance is null || SelectedInstance.Id != Owner.Snapshot.SelectedInstanceId ? "请先选定当前隔离实例。"
        : Owner.HasRunningGame ? "仅当前隔离实例可读取；写入前请先停止游戏。"
        : Service is IInstanceWorldEditService ? "仅当前选中的隔离实例可读取和保存；世界设置与数据包启停需要显式保存。"
        : "仅当前选中的隔离实例可读取和保存；level.dat 保持只读。";
    public string WorldContextTitle => IsWorldDetails ? "存档管理 · " + SelectedWorldRow?.Name : "实例设置 · " + SelectedInstance?.Name;
    public string WorldWorkspaceFeedback => _worldDraft.Feedback;
    public ObservableCollection<InstanceWorldRowViewModel> WorldRows => _worldDraft.FilteredWorlds;
    public string WorldSearchText { get => _worldDraft.WorldQuery; set { if (_worldDraft.WorldQuery == value) return; _worldDraft.WorldQuery = value; OnPropertyChanged(); FilterWorldRows(); } }
    public string WorldCountSummary => $"存档列表（{WorldRows.Count}）";
    public string WorldListStatus => _worldDraft.WorldsPhase switch { WorldReadPhase.Unread => "尚未读取当前实例的存档。", WorldReadPhase.Loading => "正在读取存档…", WorldReadPhase.Error => "读取失败，请查看错误提示后重新读取。", _ => WorldRows.Count == 0 ? "当前搜索范围内没有存档。" : $"已读取 {_worldDraft.Worlds.Count} 个存档。" };
    public bool ShowWorldListStatus => _worldDraft.WorldsPhase != WorldReadPhase.Ready || WorldRows.Count == 0;
    public InstanceWorldRowViewModel? SelectedWorldRow { get => _worldDraft.SelectedWorld; set { if (ReferenceEquals(_worldDraft.SelectedWorld, value)) return; _worldDraft.SelectedWorld = value; OnPropertyChanged(); NotifyWorldPresentation(); } }
    public string WorldDetailName => SelectedWorldRow?.Name ?? "";
    public string WorldDetailVersion => SelectedWorldRow?.Version ?? "";
    public string WorldDetailLastPlayed => SelectedWorldRow?.LastPlayed ?? "";
    public string WorldSeedText => SelectedWorldRow?.Summary.Seed?.ToString(CultureInfo.InvariantCulture) ?? "未知";
    public bool HasWorldSeed => SelectedWorldRow?.Summary.Seed is not null;
    public string WorldGameModeText => SelectedWorldRow?.Summary.GameMode switch { 0 => "生存模式", 1 => "创造模式", 2 => "冒险模式", 3 => "旁观模式", null => "未知", var value => $"未知模式（{value}）" };
    public string WorldHardcoreText => SelectedWorldRow?.Summary.Hardcore switch { true => "是", false => "否", _ => "未知" };
    public ObservableCollection<InstanceDatapackRowViewModel> DatapackRows => CurrentWorldPacks.Filtered;
    public IList SelectedDatapacks => CurrentWorldPacks.Selected;
    public string DatapackSearchText { get => CurrentWorldPacks.Query; set { var packs = CurrentWorldPacks; if (packs.Query == value) return; packs.Query = value; OnPropertyChanged(); FilterPackRows(packs); NotifyWorldPresentation(); } }
    public string DatapackCountSummary => $"全部（{DatapackRows.Count}）";
    public string DatapackSelectionSummary => $"已选择 {CurrentWorldPacks.Selected.Count} 个文件";
    public bool HasSelectedDatapacks => CurrentWorldPacks.Selected.Count > 0;
    public string DatapackListStatus => CurrentWorldPacks.Phase switch { WorldReadPhase.Unread => "尚未读取此世界的数据包文件。", WorldReadPhase.Loading => "正在读取数据包…", WorldReadPhase.Error => "读取失败，请重新读取。", _ => DatapackRows.Count == 0 ? "当前搜索范围内没有数据包文件。" : "仅列出文件；版本兼容性与启停状态未验证。" };
    public bool CanImportWorldDatapack => WorldWorkspaceCanWrite && ReferenceEquals(Owner.SelectedPage, this) && IsWorldDatapacksPage && SelectedWorldRow is { HasError: false };
    public ObservableCollection<InstanceServerDraftViewModel> InstanceServerRows => _worldDraft.Servers;
    public InstanceServerDraftViewModel? SelectedInstanceServer { get => _worldDraft.SelectedServer; set { if (ReferenceEquals(_worldDraft.SelectedServer, value)) return; _worldDraft.SelectedServer = value; OnPropertyChanged(); NotifyWorldCommands(); } }
    public bool IsServerDraftDirty => _worldDraft.ServerDirty;
    public bool HasServerConflict => _worldDraft.ServerConflict;
    public string ServerDraftStatus => _worldDraft.ServerConflict ? "服务器文件已有变化或保存未完成，当前草稿仍保留；请核对后显式放弃并重新读取。" : _worldDraft.ServerDirty ? "有未保存的服务器修改；仅点击“保存服务器”后写入文件。" : "当前没有未保存的服务器修改。";
    public string ServerListStatus => _worldDraft.ServersPhase switch { WorldReadPhase.Unread => "尚未读取当前实例的服务器列表。", WorldReadPhase.Loading => "正在读取 servers.dat…", WorldReadPhase.Error => "读取失败；原文件保持不变。", _ => InstanceServerRows.Count == 0 ? "此实例尚无服务器，可添加到草稿后显式保存。" : $"服务器（{InstanceServerRows.Count}）；在线状态与快捷连接尚未接入。" };
    public bool IsServerEditorOpen => IsInstanceServersPage && _worldDraft.EditorOpen;
    public bool IsEditingExistingServer => _worldDraft.EditingServer is not null;
    public string ServerEditorTitle => _worldDraft.EditingServer is null ? "添加服务器" : "修改服务器";
    public string ServerNameDraft { get => _worldDraft.EditorName; set { if (_worldDraft.EditorName == value) return; _worldDraft.EditorName = value; OnPropertyChanged(); NotifyWorldCommands(); } }
    public string ServerAddressDraft { get => _worldDraft.EditorAddress; set { if (_worldDraft.EditorAddress == value) return; _worldDraft.EditorAddress = value; OnPropertyChanged(); NotifyWorldCommands(); } }
    public string ServerBackupPath => UiPrivacy.Redact(_worldDraft.Backup);
    public bool HasServerBackup => _worldDraft.Backup.Length > 0;
    public AsyncRelayCommand RefreshWorldsCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshWorldDatapacksCommand { get; private set; } = null!;
    public RelayCommand ShowWorldInformationCommand { get; private set; } = null!;
    public AsyncRelayCommand ShowWorldDatapacksCommand { get; private set; } = null!;
    public RelayCommand BackFromWorldCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenWorldSavesDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenWorldDirectoryCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenWorldDatapacksDirectoryCommand { get; private set; } = null!;
    public RelayCommand SelectAllWorldDatapacksCommand { get; private set; } = null!;
    public RelayCommand ClearWorldDatapackSelectionCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshInstanceServersCommand { get; private set; } = null!;
    public AsyncRelayCommand DiscardInstanceServersCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveInstanceServersCommand { get; private set; } = null!;
    public RelayCommand AddInstanceServerCommand { get; private set; } = null!;
    public RelayCommand EditInstanceServerCommand { get; private set; } = null!;
    public RelayCommand RemoveInstanceServerCommand { get; private set; } = null!;
    public RelayCommand ApplyInstanceServerDraftCommand { get; private set; } = null!;
    public RelayCommand CancelInstanceServerEditorCommand { get; private set; } = null!;

    public void InitializeWorldWorkspace()
    {
        if (_worldWorkspaceReady) return;
        RefreshWorldsCommand = AsyncCommand(RefreshWorldsAsync, () => WorldWorkspaceCanRead);
        RefreshWorldDatapacksCommand = AsyncCommand(RefreshWorldPacksAsync, () => WorldWorkspaceCanRead && SelectedWorldRow is { HasError: false });
        ShowWorldInformationCommand = Command(() => { _worldDraft.WorldRoute = "info"; NotifyWorldPresentation(); }, () => SelectedWorldRow is { HasError: false });
        ShowWorldDatapacksCommand = AsyncCommand(async () => { _worldDraft.WorldRoute = "datapacks"; EnsurePackDraft(); NotifyWorldPresentation(); if (CurrentWorldPacks.Phase == WorldReadPhase.Unread) await RefreshWorldPacksAsync(); }, () => WorldWorkspaceCanRead && SelectedWorldRow is { HasError: false });
        BackFromWorldCommand = Command(() => { _worldDraft.WorldRoute = "list"; NotifyWorldPresentation(); });
        OpenWorldSavesDirectoryCommand = AsyncCommand(() => OpenWorldFolderAsync("saves"), () => WorldWorkspaceCanRead);
        OpenWorldDirectoryCommand = AsyncCommand(() => OpenWorldFolderAsync("saves/" + SelectedWorldRow!.DirectoryName), () => WorldWorkspaceCanRead && SelectedWorldRow is { HasError: false });
        OpenWorldDatapacksDirectoryCommand = AsyncCommand(() => OpenWorldFolderAsync("saves/" + SelectedWorldRow!.DirectoryName + "/datapacks"), () => WorldWorkspaceCanRead && SelectedWorldRow is { HasError: false });
        SelectAllWorldDatapacksCommand = Command(() => { var packs = CurrentWorldPacks; packs.Selected.Clear(); foreach (var row in packs.Filtered) packs.Selected.Add(row); }, () => DatapackRows.Count > 0);
        ClearWorldDatapackSelectionCommand = Command(() => CurrentWorldPacks.Selected.Clear());
        RefreshInstanceServersCommand = AsyncCommand(() => ReadInstanceServersAsync(false), () => WorldWorkspaceCanRead && !_worldDraft.ServerDirty && !_worldDraft.EditorOpen);
        DiscardInstanceServersCommand = AsyncCommand(() => ReadInstanceServersAsync(true), () => WorldWorkspaceCanRead && (_worldDraft.ServerDirty || _worldDraft.ServerConflict || _worldDraft.EditorOpen));
        SaveInstanceServersCommand = AsyncCommand(SaveInstanceServersDraftAsync, () => WorldWorkspaceCanWrite && _worldDraft.ServerDirty && !_worldDraft.ServerConflict && !_worldDraft.EditorOpen);
        AddInstanceServerCommand = Command(() => BeginServerEditor(null), () => WorldWorkspaceCanWrite && _worldDraft.ServersPhase == WorldReadPhase.Ready);
        EditInstanceServerCommand = Command(() => BeginServerEditor(SelectedInstanceServer), () => WorldWorkspaceCanWrite && SelectedInstanceServer is not null);
        RemoveInstanceServerCommand = Command(() => { if (SelectedInstanceServer is not { } row) return; _worldDraft.Servers.Remove(row); _worldDraft.SelectedServer = null; _worldDraft.EditorOpen = false; _worldDraft.ServerDirty = true; _worldDraft.Feedback = "已从草稿移除；尚未写入 servers.dat。"; NotifyWorldPresentation(); }, () => WorldWorkspaceCanWrite && SelectedInstanceServer is not null);
        ApplyInstanceServerDraftCommand = Command(ApplyServerEditor, () => WorldWorkspaceCanWrite && !string.IsNullOrWhiteSpace(ServerNameDraft) && !string.IsNullOrWhiteSpace(ServerAddressDraft));
        CancelInstanceServerEditorCommand = Command(() => { _worldDraft.EditorOpen = false; NotifyWorldPresentation(); });
        foreach (var command in new[] { RefreshWorldsCommand, RefreshInstanceServersCommand, RefreshWorldDatapacksCommand })
            command.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AsyncRelayCommand.IsRunning) && !command.IsRunning) ScheduleUnreadWorldSection(); };
        _worldWorkspaceReady = true; InitializeWorldEditing(); NotifyWorldWorkspace();
    }
    public void ActivateWorldWorkspace(string sectionKey)
    {
        if (!_worldWorkspaceReady) return;
        _worldSectionKey = sectionKey; NotifyWorldWorkspace();
    }
    public void NotifyWorldWorkspace()
    {
        if (!_worldWorkspaceReady) return;
        var key = SelectedInstance is { } instance ? instance.Id + "\0" + instance.GameDirectory : null;
        if (key != _worldInstanceKey)
        {
            _worldInstanceKey = key;
            if (key is null) _worldDraft = new();
            else if (!_worldDrafts.TryGetValue(key, out _worldDraft!)) _worldDrafts[key] = _worldDraft = new();
        }
        NotifyWorldPresentation();
    }
    private void NotifyWorldPresentation()
    {
        OnPropertyChanged(nameof(IsEditingExistingServer));
        foreach (var name in new[] { nameof(IsWorldWorkspaceSection), nameof(IsWorldListPage), nameof(IsWorldDetails), nameof(IsWorldInformationPage), nameof(IsWorldDatapacksPage), nameof(IsInstanceServersPage), nameof(WorldWorkspaceCanRead), nameof(WorldWorkspaceCanWrite), nameof(WorldWorkspaceCapability), nameof(WorldContextTitle), nameof(WorldWorkspaceFeedback), nameof(WorldRows), nameof(WorldSearchText), nameof(WorldCountSummary), nameof(WorldListStatus), nameof(ShowWorldListStatus), nameof(SelectedWorldRow), nameof(WorldDetailName), nameof(WorldDetailVersion), nameof(WorldDetailLastPlayed), nameof(WorldSeedText), nameof(HasWorldSeed), nameof(WorldGameModeText), nameof(WorldHardcoreText), nameof(DatapackRows), nameof(SelectedDatapacks), nameof(DatapackSearchText), nameof(DatapackCountSummary), nameof(DatapackSelectionSummary), nameof(HasSelectedDatapacks), nameof(DatapackListStatus), nameof(CanImportWorldDatapack), nameof(InstanceServerRows), nameof(SelectedInstanceServer), nameof(IsServerDraftDirty), nameof(HasServerConflict), nameof(ServerDraftStatus), nameof(ServerListStatus), nameof(IsServerEditorOpen), nameof(ServerEditorTitle), nameof(ServerNameDraft), nameof(ServerAddressDraft), nameof(ServerBackupPath), nameof(HasServerBackup) }) OnPropertyChanged(name);
        NotifyWorldCommands();
        NotifyWorldEditing();
        ScheduleUnreadWorldSection();
    }
    private void ScheduleUnreadWorldSection()
    {
        if (!_worldWorkspaceReady || _worldUnreadScheduled || !ReferenceEquals(Owner.SelectedPage, this)) return;
        if (!(IsWorldListPage && _worldDraft.WorldsPhase == WorldReadPhase.Unread || IsInstanceServersPage && _worldDraft.ServersPhase == WorldReadPhase.Unread
            || IsWorldDatapacksPage && CurrentWorldPacks.Phase == WorldReadPhase.Unread)) return;
        _worldUnreadScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _worldUnreadScheduled = false;
            if (!WorldWorkspaceCanRead || !ReferenceEquals(Owner.SelectedPage, this)) return;
            AsyncRelayCommand? command = IsWorldListPage && _worldDraft.WorldsPhase == WorldReadPhase.Unread ? RefreshWorldsCommand
                : IsInstanceServersPage && _worldDraft.ServersPhase == WorldReadPhase.Unread ? RefreshInstanceServersCommand
                : IsWorldDatapacksPage && CurrentWorldPacks.Phase == WorldReadPhase.Unread ? RefreshWorldDatapacksCommand : null;
            if (command is { IsRunning: false } && command.CanExecute(null)) command.Execute(null);
        }, DispatcherPriority.Background);
    }
    private void NotifyWorldCommands()
    {
        if (!_worldWorkspaceReady) return;
        foreach (var command in new IRelayCommand[] { RefreshWorldsCommand, RefreshWorldDatapacksCommand, ShowWorldInformationCommand, ShowWorldDatapacksCommand, BackFromWorldCommand, OpenWorldSavesDirectoryCommand, OpenWorldDirectoryCommand, OpenWorldDatapacksDirectoryCommand, SelectAllWorldDatapacksCommand, ClearWorldDatapackSelectionCommand, RefreshInstanceServersCommand, DiscardInstanceServersCommand, SaveInstanceServersCommand, AddInstanceServerCommand, EditInstanceServerCommand, RemoveInstanceServerCommand, ApplyInstanceServerDraftCommand, CancelInstanceServerEditorCommand }) command.NotifyCanExecuteChanged();
    }
    private void FilterWorldRows(InstanceWorldDraft? target = null)
    {
        var draft = target ?? _worldDraft; var query = draft.WorldQuery.Trim(); draft.FilteredWorlds.Clear();
        foreach (var row in draft.Worlds.Where(row => query.Length == 0 || (row.Name + " " + row.DirectoryName + " " + row.Version).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(row => row.DirectoryName, StringComparer.Ordinal)) draft.FilteredWorlds.Add(row);
        if (!ReferenceEquals(draft, _worldDraft)) return;
        OnPropertyChanged(nameof(WorldCountSummary)); OnPropertyChanged(nameof(WorldListStatus)); OnPropertyChanged(nameof(ShowWorldListStatus));
    }
    private async Task RefreshWorldsAsync()
    {
        if (!WorldWorkspaceCanRead || SelectedInstance is not { } instance) return;
        var draft = _worldDraft; draft.WorldsPhase = WorldReadPhase.Loading; NotifyWorldPresentation(); IReadOnlyList<InstanceWorldSummary>? rows = null;
        var completed = await Owner.RunAsync("读取实例存档", async ct => rows = await ((IInstanceWorldService)Service).GetInstanceWorldsAsync(instance.Id, ct), true);
        if (completed && rows is not null)
        {
            var selected = draft.SelectedWorld?.DirectoryName; draft.Worlds.Clear();
            foreach (var row in rows) draft.Worlds.Add(new(row, OpenWorldDetails));
            draft.SelectedWorld = draft.Worlds.FirstOrDefault(row => row.DirectoryName == selected); draft.WorldsPhase = WorldReadPhase.Ready;
            if (draft.SelectedWorld is null) draft.WorldRoute = "list";
            FilterWorldRows(draft);
        }
        else draft.WorldsPhase = WorldReadPhase.Error;
        if (ReferenceEquals(draft, _worldDraft)) NotifyWorldPresentation();
    }
    private void OpenWorldDetails(InstanceWorldRowViewModel row)
    {
        if (row.HasError || !_worldDraft.Worlds.Contains(row)) return;
        _worldDraft.SelectedWorld = row; _worldDraft.WorldRoute = "info"; EnsurePackDraft(); NotifyWorldPresentation();
    }
    private void EnsurePackDraft()
    {
        if (SelectedWorldRow is not { } row || _worldDraft.Packs.ContainsKey(row.DirectoryName)) return;
        var packs = new WorldPackDraft(); packs.Selected.CollectionChanged += (_, _) => { if (ReferenceEquals(CurrentWorldPacks, packs)) { OnPropertyChanged(nameof(HasSelectedDatapacks)); OnPropertyChanged(nameof(DatapackSelectionSummary)); NotifyWorldEditingCommands(); } };
        _worldDraft.Packs.Add(row.DirectoryName, packs);
    }
    private static void FillPacks(WorldPackDraft packs, IReadOnlyList<WorldDatapackFile> rows)
    {
        packs.Selected.Clear(); packs.Rows.Clear(); foreach (var row in rows) packs.Rows.Add(new(row)); packs.Phase = WorldReadPhase.Ready; FilterPackRows(packs);
    }
    private static void FilterPackRows(WorldPackDraft packs)
    {
        packs.Filtered.Clear(); foreach (var row in packs.Rows.Where(row => (row.Name + " " + row.Description).Contains(packs.Query.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(row => row.Name, StringComparer.Ordinal)) packs.Filtered.Add(row);
    }
    private async Task RefreshWorldPacksAsync()
    {
        if (!WorldWorkspaceCanRead || SelectedInstance is not { } instance || SelectedWorldRow is not { HasError: false } world) return;
        EnsurePackDraft(); var packs = CurrentWorldPacks; packs.Phase = WorldReadPhase.Loading; NotifyWorldPresentation(); IReadOnlyList<WorldDatapackFile>? rows = null;
        var completed = await Owner.RunAsync("读取世界数据包", async ct => rows = await ((IInstanceWorldService)Service).GetWorldDatapacksAsync(instance.Id, world.DirectoryName, ct), true);
        if (completed && rows is not null) FillPacks(packs, rows); else packs.Phase = WorldReadPhase.Error;
        if (ReferenceEquals(CurrentWorldPacks, packs)) NotifyWorldPresentation();
        if (completed && ReferenceEquals(CurrentWorldPacks, packs) && Service is IInstanceWorldEditService && !IsWorldSettingsDirty) await ReadWorldEditStateAsync(false);
    }
    public async Task<bool> ImportWorldDatapackFileAsync(string path, string? expectedInstanceId = null, string? expectedWorldDirectory = null, string? expectedGameDirectory = null)
    {
        if (expectedInstanceId is not null && expectedInstanceId != SelectedInstance?.Id || expectedWorldDirectory is not null && expectedWorldDirectory != SelectedWorldRow?.DirectoryName
            || expectedGameDirectory is not null && expectedGameDirectory != SelectedInstance?.GameDirectory)
        { _worldDraft.Feedback = "选择目标已经改变，未导入文件。"; NotifyWorldPresentation(); return false; }
        if (!CanImportWorldDatapack || SelectedInstance is not { } instance || SelectedWorldRow is not { } world)
        { _worldDraft.Feedback = "当前目标不可导入。" + WorldWorkspaceCapability; NotifyWorldPresentation(); return false; }
        var draft = _worldDraft; var packs = CurrentWorldPacks; WorldDatapackFile? imported = null; IReadOnlyList<WorldDatapackFile>? rows = null;
        var completed = await Owner.RunAsync("导入世界数据包文件", async ct =>
        {
            imported = await ((IInstanceWorldService)Service).ImportWorldDatapackAsync(instance.Id, world.DirectoryName, path, ct);
            rows = await ((IInstanceWorldService)Service).GetWorldDatapacksAsync(instance.Id, world.DirectoryName, ct);
        });
        if (rows is not null) FillPacks(packs, rows);
        draft.Feedback = completed && imported is not null ? $"已导入 {imported.Name}。{imported.Status}" : imported is not null ? "文件已导入，但列表刷新失败，请重新读取。" : "导入未完成，请重新读取核对文件列表。";
        if (ReferenceEquals(draft, _worldDraft)) NotifyWorldPresentation();
        if (completed && ReferenceEquals(draft, _worldDraft) && Service is IInstanceWorldEditService && !IsWorldSettingsDirty) await ReadWorldEditStateAsync(false);
        return completed;
    }
    private async Task ReadInstanceServersAsync(bool discardDraft)
    {
        if (!WorldWorkspaceCanRead || SelectedInstance is not { } instance) return;
        var draft = _worldDraft;
        if (!discardDraft && (draft.ServerDirty || draft.EditorOpen)) { draft.Feedback = "草稿仍保留，请先保存或显式放弃并重新读取。"; NotifyWorldPresentation(); return; }
        draft.ServersPhase = WorldReadPhase.Loading; NotifyWorldPresentation(); InstanceServerList? result = null;
        var completed = await Owner.RunAsync("读取实例服务器", async ct => result = await ((IInstanceWorldService)Service).GetInstanceServersAsync(instance.Id, ct), true);
        if (completed && result is not null) LoadServerList(draft, result);
        else { draft.ServersPhase = WorldReadPhase.Error; draft.Feedback = "读取未完成，已有草稿仍保留。"; }
        if (ReferenceEquals(draft, _worldDraft)) NotifyWorldPresentation();
    }
    private static void LoadServerList(InstanceWorldDraft draft, InstanceServerList list)
    {
        draft.Servers.Clear(); foreach (var row in list.Entries) draft.Servers.Add(new(Guid.NewGuid(), row.ExistingIndex, row.Name, row.Address));
        draft.ServerRevision = list.Revision; draft.ServerDirty = false; draft.ServerConflict = false; draft.ServersPhase = WorldReadPhase.Ready; draft.EditorOpen = false; draft.EditingServer = null; draft.SelectedServer = null;
        if (list.BackupPath is not null) draft.Backup = list.BackupPath;
    }
    private void BeginServerEditor(InstanceServerDraftViewModel? server)
    {
        _worldDraft.EditingServer = server; _worldDraft.EditorName = server?.Name ?? ""; _worldDraft.EditorAddress = server?.Address ?? ""; _worldDraft.EditorOpen = true; NotifyWorldPresentation();
    }
    private void ApplyServerEditor()
    {
        var name = ServerNameDraft.Trim(); var address = ServerAddressDraft.Trim();
        if (name.Length is 0 or > 128 || name.Any(char.IsControl) || address.Length is 0 or > 1024 || address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        { Owner.ShowError("请填写有效的服务器名称与地址。名称最多128字符，地址不能包含空白。"); return; }
        var previous = _worldDraft.EditingServer; var row = new InstanceServerDraftViewModel(previous?.DraftId ?? Guid.NewGuid(), previous?.ExistingIndex, name, address);
        if (previous is not null && _worldDraft.Servers.IndexOf(previous) is var index && index >= 0) _worldDraft.Servers[index] = row; else _worldDraft.Servers.Add(row);
        _worldDraft.SelectedServer = row; _worldDraft.EditorOpen = false; _worldDraft.ServerDirty = true; _worldDraft.Feedback = "已更新草稿；尚未写入 servers.dat。"; NotifyWorldPresentation();
    }
    private async Task SaveInstanceServersDraftAsync()
    {
        if (!WorldWorkspaceCanWrite || !_worldDraft.ServerDirty || _worldDraft.ServerConflict || _worldDraft.EditorOpen || SelectedInstance is not { } instance) return;
        var draft = _worldDraft; var entries = draft.Servers.Select(row => new InstanceServerEdit(row.ExistingIndex, row.Name, row.Address)).ToArray(); InstanceServerList? saved = null; var conflict = false;
        var completed = await Owner.RunAsync("保存当前实例服务器", async ct =>
        {
            var service = (IInstanceWorldService)Service;
            var current = await service.GetInstanceServersAsync(instance.Id, ct);
            if (current.Revision != draft.ServerRevision) { conflict = true; return; }
            try { saved = await service.SaveInstanceServersAsync(instance.Id, draft.ServerRevision, entries, ct); }
            catch (IOException) { conflict = true; throw; }
        });
        if (completed && saved is not null) { LoadServerList(draft, saved); draft.Feedback = "服务器列表已保存。"; }
        else { draft.ServerConflict = conflict; draft.Feedback = conflict ? "文件已有外部修改或保存未完成；当前草稿仍保留，请核对后重新读取。" : "保存未完成，当前草稿仍保留。"; }
        if (ReferenceEquals(draft, _worldDraft)) NotifyWorldPresentation();
    }
    private Task OpenWorldFolderAsync(string relative)
    {
        if (!WorldWorkspaceCanRead || SelectedInstance is not { } instance) return Task.CompletedTask;
        return Owner.RunControlAsync("打开存档文件夹", async ct =>
        {
            var game = SafePath.Canonical(instance.GameDirectory); var path = SafePath.Within(game, relative);
            if (!SafePath.IsWithin(SafePath.Canonical(Service.Paths.DataDirectory), game) || !Directory.Exists(path)) throw new IOException("文件夹不存在或不在当前隔离实例内。");
            await Owner.ExternalLauncher.OpenDirectoryAsync(path, ct);
        });
    }
}
