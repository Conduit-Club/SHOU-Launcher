using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.Localization;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private InstanceModRelations? modRelations;
    private CancellationTokenSource? relationCancellation;
    private readonly List<Task> relationReads = [];
    private int relationGeneration;
    private bool relationsBusy, relationIssuesOnly;
    private string relationSearch = "", relationStatus = "";
    private ModRelationHostViewModel? selectedRelationHost;
    private IReadOnlyList<ModRelationHostViewModel> relationHosts = [];
    public bool IsRelationsSection => !IsSelection && SelectedSection?.Key == "relations";
    public bool IsModRelationsBusy { get => relationsBusy; private set { SetProperty(ref relationsBusy, value); RefreshModRelationsCommand?.NotifyCanExecuteChanged(); CancelModRelationsCommand?.NotifyCanExecuteChanged(); } }
    public string ModRelationSearch { get => relationSearch; set { if (SetProperty(ref relationSearch, value)) FilterModRelations(); } }
    public bool ModRelationIssuesOnly { get => relationIssuesOnly; set { if (SetProperty(ref relationIssuesOnly, value)) FilterModRelations(); } }
    public string ModRelationStatus { get => relationStatus; private set => SetProperty(ref relationStatus, value); }
    public string ModRelationSummary => modRelations is null ? "" : UiLocalization.Instance.Format("{0} 个文件 · {1} 个声明节点 · {2} 项依赖", modRelations.Hosts.Count, modRelations.Hosts.Sum(h => ModRelations.Flatten(h.Declaration).Count()), modRelations.Dependencies.Count);
    public IReadOnlyList<ModRelationHostViewModel> FilteredModRelationHosts => relationHosts.Where(h => (!ModRelationIssuesOnly || h.HasIssues) && (ModRelationSearch.Trim().Length == 0 || h.Search.Contains(ModRelationSearch.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
    public bool ModRelationsEmpty => !IsModRelationsBusy && FilteredModRelationHosts.Count == 0;
    public ModRelationHostViewModel? SelectedModRelationHost { get => selectedRelationHost; set { SetProperty(ref selectedRelationHost, value); OnPropertyChanged(nameof(HasSelectedModRelationHost)); } }
    public bool HasSelectedModRelationHost => SelectedModRelationHost is not null;
    public AsyncRelayCommand RefreshModRelationsCommand { get; private set; } = null!;
    public RelayCommand CancelModRelationsCommand { get; private set; } = null!;
    public RelayCommand BackToModsCommand { get; private set; } = null!;
    private void InitializeModRelations()
    {
        RefreshModRelationsCommand = AsyncCommand(RefreshModRelationsAsync, () => IsRelationsSection && SelectedInstance is not null && !IsModRelationsBusy && Service is IInstanceModRelationsService);
        CancelModRelationsCommand = Command(() => { StopModRelations(); ModRelationStatus = UiLocalization.Instance.Text("读取已取消，可重新读取。"); }, () => IsModRelationsBusy);
        BackToModsCommand = Command(() => SelectedSection = Sections.Single(s => s.Key == "mods"));
    }
    private void FilterModRelations()
    {
        var filtered = FilteredModRelationHosts;
        var selection = filtered.FirstOrDefault(h => h.File == selectedRelationHost?.File) ?? filtered.FirstOrDefault();
        // Populate the ComboBox before selecting: a selection published against its old items is discarded.
        OnPropertyChanged(nameof(FilteredModRelationHosts));
        SelectedModRelationHost = selection;
        OnPropertyChanged(nameof(SelectedModRelationHost));
        OnPropertyChanged(nameof(ModRelationsEmpty)); OnPropertyChanged(nameof(ModRelationSummary));
    }
    private void StopModRelations()
    {
        ++relationGeneration; relationCancellation?.Cancel(); relationCancellation = null; IsModRelationsBusy = false;
    }
    private void ClearModRelations()
    {
        StopModRelations(); modRelations = null; relationHosts = []; SelectedModRelationHost = null; ModRelationStatus = ""; FilterModRelations();
    }
    private void ScheduleModRelations()
    {
        if (!IsRelationsSection || _workspaceDisposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_workspaceDisposed || !IsRelationsSection || !ReferenceEquals(Owner.SelectedPage, this) || IsModRelationsBusy) return;
            if (Service is not IInstanceModRelationsService) { ModRelationStatus = UiLocalization.Instance.Text("当前服务不支持读取模组关系。"); return; }
            relationReads.RemoveAll(t => t.IsCompleted); relationReads.Add(RefreshModRelationsCommand.ExecuteAsync(null));
        }, DispatcherPriority.Background);
    }
    private async Task RefreshModRelationsAsync()
    {
        if (SelectedInstance is not { } instance || Service is not IInstanceModRelationsService service || !IsRelationsSection) return;
        StopModRelations(); var generation = relationGeneration; var source = new CancellationTokenSource(); relationCancellation = source;
        bool Current() => !_workspaceDisposed && generation == relationGeneration && IsRelationsSection && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && ReferenceEquals(Owner.SelectedPage, this);
        IsModRelationsBusy = true; ModRelationStatus = UiLocalization.Instance.Text("正在读取模组与内嵌声明…");
        try
        {
            var result = await service.ReadInstanceModRelationsAsync(instance.Id, source.Token);
            if (!Current() || source.IsCancellationRequested) return;
            modRelations = result; relationHosts = result.Hosts.Select(h => new ModRelationHostViewModel(h, result.Dependencies.Where(d => d.HostPath == h.RelativePath).ToArray())).ToArray();
            ModRelationStatus = UiLocalization.Instance.Text(result.Notices.Count > 0 ? "部分声明未能读取，请查看文件详情。" : "声明读取完成。"); FilterModRelations();
        }
        catch (OperationCanceledException) { if (Current()) ModRelationStatus = UiLocalization.Instance.Text("读取已取消，可重新读取。"); }
        catch (Exception ex) { if (Current()) ModRelationStatus = UiLocalization.Instance.Text("读取失败：") + UiPrivacy.Redact(ex.Message); }
        finally { if (Current()) { IsModRelationsBusy = false; OnPropertyChanged(nameof(ModRelationsEmpty)); } if (ReferenceEquals(relationCancellation, source)) relationCancellation = null; source.Dispose(); }
    }
}

public sealed class ModRelationHostViewModel
{
    public ModRelationHostViewModel(ModRelationHost host, IReadOnlyList<ModDependencyRelation> dependencies)
    {
        Name = host.Declaration.Name; File = host.RelativePath; Enabled = host.Enabled;
        Nodes = Flatten(host.Declaration, 0).ToArray(); Dependencies = dependencies.Select(d => new ModRelationDependencyViewModel(d)).ToArray();
        HasIssues = Nodes.Any(n => n.Notices.Length > 0) || dependencies.Any(d => d.OwnerEnabled && !d.Dependency.Optional && d.Presence is ModDeclarationPresence.Disabled or ModDeclarationPresence.NotFound or ModDeclarationPresence.Unverified);
        Search = string.Join("\n", Nodes.Select(n => n.Title + " " + n.Path + " " + n.Ids).Concat(Dependencies.Select(d => d.Description + " " + d.Providers)));
    }
    public string Name { get; }
    public string File { get; }
    public bool Enabled { get; }
    public string State => UiLocalization.Instance.Text(Enabled ? "已启用" : "已禁用");
    public bool HasIssues { get; }
    public string Search { get; }
    public IReadOnlyList<ModRelationNodeViewModel> Nodes { get; }
    public IReadOnlyList<ModRelationDependencyViewModel> Dependencies { get; }
    public bool HasDependencies => Dependencies.Count > 0;
    public override string ToString() => Name;
    private static IEnumerable<ModRelationNodeViewModel> Flatten(ModDeclaration node, int depth)
    { yield return new(node, depth); foreach (var child in node.Children) foreach (var row in Flatten(child, depth + 1)) yield return row; }
}
public sealed class ModRelationNodeViewModel(ModDeclaration node, int depth)
{
    public Thickness Indent => new(depth * 12, 0, 0, 0);
    public string Title => node.Name + (node.Version is { } v ? " · " + v : "");
    public string Ids => string.Join(", ", node.Ids);
    public string Path => node.Path;
    public string Notices => string.Join("\n", node.Notices.Select(UiLocalization.Instance.Text));
    public bool HasNotices => node.Notices.Count > 0;
}
public sealed class ModRelationDependencyViewModel(ModDependencyRelation relation)
{
    public string Description => relation.OwnerName + " → " + relation.Dependency.Id + (relation.Dependency.Requirement is { } r ? "  " + r : "") + (relation.Dependency.Optional ? " · " + UiLocalization.Instance.Text("可选") : "");
    public string Presence => UiLocalization.Instance.Text(relation.Presence switch { ModDeclarationPresence.Independent => "找到独立声明", ModDeclarationPresence.Embedded => "找到内嵌声明", ModDeclarationPresence.Disabled => "仅在已禁用文件中找到", ModDeclarationPresence.NotFound => "未找到声明", _ => "读取不完整，尚未核实" });
    public string Providers => string.Join("\n", relation.Providers);
    public bool HasProviders => relation.Providers.Count > 0;
}
