using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    private bool componentsInitialized, componentLoading, componentChanging, confirmComponentChange;
    private CancellationTokenSource? componentRead;
    private string componentInstance = "", componentReadKey = "", componentFeedback = "";
    private InstanceLoaderChoice componentLoader = new("fabric", "Fabric");
    private LoaderVersionChoice? componentVersion;
    private TaskCompletionSource? componentDone;
    public IReadOnlyList<InstanceLoaderChoice> ComponentLoaderChoices { get; } = [new("vanilla", "原版"), new("fabric", "Fabric"), new("quilt", "Quilt"), new("forge", "Forge"), new("neoforge", "NeoForge")];
    public ObservableCollection<LoaderVersionChoice> ComponentVersions { get; } = [];
    public InstanceLoaderChoice ComponentLoader { get => componentLoader; set { if (value is null || !SetProperty(ref componentLoader, value)) return; componentReadKey = ""; ConfirmComponentChange = false; OnPropertyChanged(nameof(ComponentNeedsVersion)); ScheduleComponentRefresh(); NotifyCommands(); } }
    public LoaderVersionChoice? ComponentVersion { get => componentVersion; set { if (SetProperty(ref componentVersion, value)) { ConfirmComponentChange = false; NotifyCommands(); } } }
    public bool ComponentNeedsVersion => ComponentLoader.Id != "vanilla";
    public bool ConfirmComponentChange { get => confirmComponentChange; set { if (SetProperty(ref confirmComponentChange, value)) NotifyCommands(); } }
    public bool IsComponentBusy => componentLoading || componentChanging;
    public string ComponentFeedback { get => componentFeedback; private set => SetProperty(ref componentFeedback, value); }
    public AsyncRelayCommand RefreshComponentVersionsCommand { get; private set; } = null!;
    public AsyncRelayCommand ChangeInstanceComponentCommand { get; private set; } = null!;
    public RelayCommand CancelComponentWorkCommand { get; private set; } = null!;
    private void InitializeComponents()
    {
        RefreshComponentVersionsCommand = AsyncCommand(() => ReadComponentVersionsAsync(true), () => !IsComponentBusy && CanEdit && SelectedInstance is not null);
        ChangeInstanceComponentCommand = AsyncCommand(ChangeComponentAsync, () => CanEdit && !IsSelectedInstanceRunning && !IsComponentBusy && ConfirmComponentChange && SelectedInstance is not null
            && Service is IInstanceComponentService && (!ComponentNeedsVersion || ComponentVersion is { CanInstall: true }));
        CancelComponentWorkCommand = Command(() => { componentRead?.Cancel(); if (componentChanging) Owner.CancelOperationCommand.Execute(null); }, () => IsComponentBusy);
        componentsInitialized = true;
    }
    private void ScheduleComponentRefresh()
    {
        if (!componentsInitialized) return;
        if (!IsModify || SelectedInstance is not { } instance) { componentRead?.Cancel(); return; }
        if (componentInstance != instance.Id)
        {
            componentInstance = instance.Id; componentLoader = ComponentLoaderChoices.FirstOrDefault(c => c.Id == instance.Loader) ?? ComponentLoaderChoices[0];
            OnPropertyChanged(nameof(ComponentLoader)); OnPropertyChanged(nameof(ComponentNeedsVersion)); ConfirmComponentChange = false; componentReadKey = "";
        }
        if (!IsComponentBusy && componentReadKey != instance.Id + ":" + ComponentLoader.Id) _ = ReadComponentVersionsAsync(false);
    }
    private async Task ReadComponentVersionsAsync(bool force)
    {
        if (SelectedInstance is not { } instance || componentChanging) return;
        if (Service is not ILoaderInstanceService catalog) { ComponentFeedback = "当前服务不支持组件目录。"; return; }
        var loader = ComponentLoader.Id; var key = instance.Id + ":" + loader;
        if (!force && componentReadKey == key) return;
        componentRead?.Cancel(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); componentRead = cts;
        componentLoading = true; NotifyComponentState(); ComponentFeedback = "";
        try
        {
            IReadOnlyList<LoaderVersionChoice> versions = loader == "vanilla" ? [] : await catalog.GetLoaderVersionsAsync(loader, Core.Instances.InstanceIdentity.GameVersion(instance), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (SelectedInstance?.Id != instance.Id || ComponentLoader.Id != loader || !IsModify) return;
            ComponentVersions.Clear(); foreach (var version in versions.Where(v => v.CanInstall)) ComponentVersions.Add(version);
            ComponentVersion = ComponentVersions.FirstOrDefault(v => v.Version == instance.LoaderVersion) ?? ComponentVersions.FirstOrDefault(v => v.Stable) ?? ComponentVersions.FirstOrDefault();
            componentReadKey = key;
            if (loader != "vanilla" && ComponentVersions.Count == 0) ComponentFeedback = "此 Minecraft 版本暂无可用加载器版本。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or NotSupportedException or InvalidOperationException or AggregateException)
        { ComponentFeedback = UiPrivacy.Redact(ex.Message); }
        finally { if (ReferenceEquals(componentRead, cts)) { componentRead = null; componentLoading = false; NotifyComponentState(); if (SelectedInstance?.Id != instance.Id) ScheduleComponentRefresh(); } }
    }
    private async Task ChangeComponentAsync()
    {
        if (SelectedInstance is not { } instance || Service is not IInstanceComponentService service) return;
        var loader = ComponentLoader.Id; var version = ComponentVersion?.Version;
        componentChanging = true; var done = componentDone = new(TaskCreationOptions.RunContinuationsAsynchronously); NotifyComponentState();
        try
        {
            var success = await Owner.RunInlineAsync("修改实例组件", ct => service.ChangeInstanceLoaderAsync(instance.Id, loader, version, ct));
            ComponentFeedback = success ? "实例组件已修改；原有模组和资源文件保留。" : "修改未完成，原实例配置仍保留。";
            ConfirmComponentChange = false;
        }
        finally { componentChanging = false; done.TrySetResult(); componentDone = null; NotifyComponentState(); }
    }
    private void NotifyComponentState() { OnPropertyChanged(nameof(IsComponentBusy)); NotifyCommands(); }
    public async Task CancelComponentWorkAsync()
    { componentRead?.Cancel(); var pending = componentDone?.Task; if (componentChanging) Owner.CancelOperationCommand.Execute(null); if (pending is not null) await pending; }
    private void DisposeComponents() => componentRead?.Cancel();
}
