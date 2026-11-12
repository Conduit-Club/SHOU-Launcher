using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class InstancesPageViewModel
{
    public Avalonia.Thickness ResourceListBottomMargin => HasModSelection ? new(0, 0, 0, 84) : default;
    public bool IsResourceBrowserSection => IsContentList && SelectedSection.Key is "mods" or "resourcepacks" or "shaders";
    public int ResourceToolbarColumns => IsModsSection ? 6 : IsResourceBrowserSection ? 5 : 4;
    public bool HasSelectedMods => IsModsSection && HasModSelection;
    public bool IsResourceUpdateSection => IsResourceBrowserSection && !IsModsSection;
    public bool CanUpdateContent => !_workspaceDisposed && ReferenceEquals(Owner.SelectedPage, this) && CanEdit && !IsSelectedInstanceRunning && !IsModScanBusy &&
        (IsModsSection && Service is IInstanceModUpdateService || IsResourceUpdateSection && Service is IInstanceResourceUpdateService);
    public string CheckContentUpdatesCaption => Localization.UiLocalization.Instance.Text(IsModsSection ? "检查模组更新" : "检查资源更新");
    public bool CanTrashMods => CanManageMods && Service is IInstanceModManagementService;
    private Task RequestContentRowUpdateAsync(InstanceContentRowViewModel row)
        => CurrentContentRow(row) ? RequestContentUpdatesAsync(row.Update is { } u ? [u] : []) : Task.CompletedTask;
    private async Task RequestContentUpdatesAsync(IReadOnlyList<InstanceModUpdate> updates)
    {
        if (!CanUpdateContent) return;
        var current = Rows();
        var valid = updates.Where(u => u.InstanceId == SelectedInstance?.Id && ModUpdates.Any(known => known.Token == u.Token) && current.Any(r => r.Update?.Token == u.Token)).ToArray();
        if (IsModsSection) { RequestModUpdate(valid); return; }
        if (valid.Length == 0 || SelectedInstance is not { } instance || Service is not IInstanceResourceUpdateService service) return;
        var section = SelectedSection.Key; var completed = 0; var failures = new List<string>();
        await Owner.RunAsync("更新资源并备份", async ct =>
        {
            await StopModScanAsync();
            foreach (var update in valid)
            {
                ct.ThrowIfCancellationRequested();
                if (SelectedInstance?.Id != instance.Id || SelectedSection.Key != section) break;
                try { await service.ApplyInstanceResourceUpdateAsync(update.Token, ct); completed++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or AggregateException)
                { failures.Add(update.Name + "：" + UiPrivacy.Redact(ex.Message)); }
            }
        });
        Feedback = Localization.UiLocalization.Instance.Format("已更新 {0}/{1} 项资源，原文件已备份。", completed, valid.Length) + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : "");
        if (SelectedInstance?.Id == instance.Id && SelectedSection.Key == section) await RefreshContentCommand.ExecuteAsync(null);
    }
    private bool CurrentContentRow(InstanceContentRowViewModel row) => !_workspaceDisposed && ReferenceEquals(Owner.SelectedPage, this) && IsContentList && SelectedInstance is not null && SelectedSection.Key == row.Entry.Kind && contentRows.TryGetValue(row.Entry.RelativePath, out var current) && ReferenceEquals(current, row);
    public void ToggleContentSelection(InstanceContentRowViewModel row)
    { if (CurrentContentRow(row)) row.Selected = !row.Selected; }
    private Task RevealContentRowAsync(InstanceContentRowViewModel row)
        => CurrentContentRow(row) ? Owner.RunControlAsync("显示资源文件", ct => Owner.ExternalLauncher.RevealFileAsync(row.Entry.FullPath, ct)) : Task.CompletedTask;
    private async Task ToggleContentRowAsync(InstanceContentRowViewModel row)
    {
        if (!CanManageMods || !CurrentContentRow(row) || Service is not IInstanceWorkspaceService service || SelectedInstance is not { } instance) return;
        var entry = row.Entry;
        if (await Owner.RunAsync(entry.Enabled ? "禁用模组" : "启用模组", async ct =>
        {
            await StopModScanAsync();
            if (SelectedInstance?.Id != instance.Id || !CurrentContentRow(row)) return;
            await service.SetInstanceModEnabledAsync(instance.Id, entry.RelativePath, !entry.Enabled, ct);
        }) && SelectedInstance?.Id == instance.Id && IsModsSection) await RefreshContentCommand.ExecuteAsync(null);
    }
    private void RequestModTrash(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        pendingModUpdates = []; pendingModPaths = paths.ToArray();
        ModActionSummary = Localization.UiLocalization.Instance.Text("将所选模组移到实例回收站？") + "\n" + string.Join("\n", pendingModPaths);
        OnPropertyChanged(nameof(ModActionCaption)); NotifyModActions();
    }
    private async Task ReadResourceMetadataAsync()
    {
        var instance = SelectedInstance!; var kind = SelectedSection.Key; StopModScan(); var generation = ++modCheckGeneration;
        var source = new CancellationTokenSource(); modScanCancellation = source; IsModScanBusy = true;
        bool Current() => !_workspaceDisposed && generation == modCheckGeneration && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory && SelectedSection.Key == kind && ReferenceEquals(Owner.SelectedPage, this);
        ModUpdateStatus = Localization.UiLocalization.Instance.Text("正在核对资源展示信息，本机列表可继续使用。");
        try
        {
            var result = await ((IInstanceContentMetadataService)Service).ReadInstanceContentMetadataAsync(instance.Id, kind, source.Token);
            if (!Current()) return;
            foreach (var online in result.Info) if (contentRows.TryGetValue(online.RelativePath, out var row))
            { row.Project = online.Project; row.Entry = row.Entry with { ModVersion = online.Version }; }
            NotifyContentFilter(); ModUpdateStatus = Localization.UiLocalization.Instance.Format("已核实 {0} 项资源展示信息；{1} 项保留本机信息。", result.Info.Count, result.Notices.Count);
            if (Service is ILauncherCatalogService icons) foreach (var row in Rows().Where(r => !r.Entry.HasIcon && r.Project?.IconUrl is not null))
            {
                if (!Current()) break;
                try { var bytes = await icons.GetContentIconAsync(row.Project!.IconUrl!, source.Token); if (Current()) row.Entry = row.Entry with { IconPng = bytes }; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or AggregateException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current()) ModUpdateStatus = Localization.UiLocalization.Instance.Text("资源展示信息暂不可用，本机列表保留。") + " " + UiPrivacy.Redact(ex.Message); }
        finally { if (Current()) IsModScanBusy = false; if (ReferenceEquals(modScanCancellation, source)) modScanCancellation = null; source.Dispose(); }
    }
}
