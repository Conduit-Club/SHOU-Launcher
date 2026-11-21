using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed partial class WpfWorldEditingUiTests
{
    private static void PrepareSortablePacks(Fixture f)
    {
        var packs = Path.Combine(Path.GetDirectoryName(f.LevelOne)!, "datapacks"); File.Copy(Path.Combine(packs, "fixture.zip"), Path.Combine(packs, "second.zip"));
        var doc = BoundedNbt.Read(File.ReadAllBytes(f.LevelOne)); var data = doc.Root.Find("Data")!;
        var enabled = new NbtTag(NbtType.List, "Enabled", new NbtList(NbtType.String, [new(NbtType.String, "", "vanilla"), new(NbtType.String, "", "file/fixture.zip"), new(NbtType.String, "", "file/second.zip")]));
        var lists = data.Find("DataPacks")!; var changed = data with { Value = data.Children.Where(c => c.Name != "DataPacks").Append(lists with { Value = lists.Children.Where(c => c.Name is not ("Enabled" or "Disabled")).Concat(new[] { enabled, new NbtTag(NbtType.List, "Disabled", new NbtList(NbtType.String, [])) }).ToArray() }).ToArray() };
        File.WriteAllBytes(f.LevelOne, BoundedNbt.Write(doc with { Root = doc.Root with { Value = doc.Root.Children.Where(c => c.Name != "Data").Append(changed).ToArray() } }));
    }
    private static async Task OpenPackManagement(Fixture f)
    { await f.OpenDetailsAsync(); await f.Vm.ShowWorldDatapacksCommand.ExecuteAsync(null); Pump(f.Window); f.Vm.SelectedDatapacks.Add(f.Vm.DatapackRows[0]); Pump(f.Window); }

    [AvaloniaFact]
    public async Task ActualOrderControlsWriteOnlyAfterSaveAndFreshViewModelReadsStoredOrder()
    {
        using var f = new Fixture(); PrepareSortablePacks(f); await OpenPackManagement(f); var original = File.ReadAllBytes(f.LevelOne);
        Click(f.Window, "ReadDatapackOrderButton"); await f.Vm.ReadDatapackOrderCommand.ExecutionTask!; Pump(f.Window);
        Assert.True(Find<Border>(f.Window, "DatapackOrderPanel").IsEffectivelyVisible); Assert.Equal(2, f.Vm.DatapackOrderItems.Count);
        Find<ListBox>(f.Window, "DatapackOrderList").SelectedItem = f.Vm.DatapackOrderItems[1]; Pump(f.Window); Click(f.Window, "MoveDatapackOrderUpButton");
        Assert.True(f.Vm.IsDatapackOrderDirty); Assert.Equal(original, File.ReadAllBytes(f.LevelOne)); Assert.Equal(0, f.Service.OrderSaves);
        Assert.False(f.Vm.EnableWorldDatapacksCommand.CanExecute(null)); Assert.False(f.Vm.ReadDatapackUpdatesCommand.CanExecute(null)); Assert.False(f.Vm.CanImportWorldDatapack); Assert.False(f.Vm.RequestRecycleWorldDatapacksCommand.CanExecute(null));
        Click(f.Window, "SaveDatapackOrderButton"); await f.Vm.SaveDatapackOrderCommand.ExecutionTask!; Pump(f.Window);
        Assert.Equal(1, f.Service.OrderSaves); Assert.False(f.Vm.IsDatapackOrderDirty); Assert.Equal("second.zip", f.Vm.DatapackOrderItems[0].Name); Assert.Equal(original, File.ReadAllBytes(f.Service.LastOrderBackup!)); Assert.False(f.Root.ShowOperationStatus);
        using var reopened = new MainWindowViewModel(f.Service, new FakeExternalLauncher()); var vm = reopened.Instances;
        reopened.SelectedPage = vm; vm.InitializeWorldWorkspace(); vm.NotifyWorldWorkspace(); vm.ShowInstanceSettingsCommand.Execute(null); vm.ActivateWorldWorkspace("worlds");
        Dispatcher.UIThread.RunJobs(); if (vm.RefreshWorldsCommand.ExecutionTask is { } read) await read; Dispatcher.UIThread.RunJobs(); vm.WorldRows.Single().OpenCommand.Execute(null);
        Dispatcher.UIThread.RunJobs(); if (vm.RefreshWorldEditCommand.ExecutionTask is { } edit) await edit; await vm.ShowWorldDatapacksCommand.ExecuteAsync(null); await vm.ReadDatapackOrderCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "second.zip", "fixture.zip" }, vm.DatapackOrderItems.Select(p => p.Name)); Assert.Equal(f.LevelTwoBytes, File.ReadAllBytes(f.LevelTwo));
    }
    [AvaloniaFact]
    public async Task FailedOrderSaveRetainsDraftAndCancelDiscardsWithoutWriting()
    {
        using var f = new Fixture(); PrepareSortablePacks(f); await OpenPackManagement(f); await f.Vm.ReadDatapackOrderCommand.ExecuteAsync(null); var original = File.ReadAllBytes(f.LevelOne);
        f.Vm.SelectedDatapackOrderItem = f.Vm.DatapackOrderItems[1]; f.Vm.MoveDatapackOrderUpCommand.Execute(null); File.SetAttributes(f.LevelOne, FileAttributes.ReadOnly);
        try { await f.Vm.SaveDatapackOrderCommand.ExecuteAsync(null); } finally { File.SetAttributes(f.LevelOne, FileAttributes.Normal); }
        Assert.True(f.Vm.IsDatapackOrderDirty); Assert.Equal(original, File.ReadAllBytes(f.LevelOne)); Assert.True(f.Root.HasError);
        Click(f.Window, "CancelDatapackManagementButton"); Assert.False(f.Vm.IsDatapackOrderDirty); Assert.False(f.Vm.IsDatapackOrderOpen); Assert.Equal(original, File.ReadAllBytes(f.LevelOne));
        Assert.True(Find<Button>(f.Window, "ReadDatapackUpdatesButton").IsEnabled);
        Assert.True(Find<Button>(f.Window, "ImportWorldDatapackButton").IsEnabled);
    }
    [AvaloniaFact]
    public async Task UpdateControlsRequireExplicitPreviewAndConfirmationAndInvalidateChangedVersion()
    {
        using var f = new Fixture(); await OpenPackManagement(f); var original = File.ReadAllBytes(f.LevelOne);
        Click(f.Window, "ReadDatapackUpdatesButton"); await f.Vm.ReadDatapackUpdatesCommand.ExecutionTask!; Pump(f.Window);
        Assert.True(Find<Border>(f.Window, "DatapackUpdatePanel").IsEffectivelyVisible); Assert.Equal("new", f.Vm.SelectedDatapackUpdateChoice!.ReleaseId); Assert.False(f.Vm.HasDatapackUpdatePreview);
        Click(f.Window, "PreviewDatapackUpdateButton"); await f.Vm.PreviewDatapackUpdateCommand.ExecutionTask!; Pump(f.Window); Assert.True(f.Vm.HasDatapackUpdatePreview); Assert.Equal(0, f.Service.UpdateApplies);
        Find<ComboBox>(f.Window, "DatapackUpdateVersionSelector").SelectedItem = f.Vm.DatapackUpdateChoices[1]; Pump(f.Window);
        Assert.False(f.Vm.HasDatapackUpdatePreview); Assert.Contains("update-1", f.Service.DiscardedUpdates); Assert.False(f.Vm.ConfirmDatapackUpdateCommand.CanExecute(null));
        await f.Vm.PreviewDatapackUpdateCommand.ExecuteAsync(null); Click(f.Window, "ConfirmDatapackUpdateButton"); await f.Vm.ConfirmDatapackUpdateCommand.ExecutionTask!; Pump(f.Window);
        Assert.Equal(1, f.Service.UpdateApplies); Assert.Equal("older", f.Service.LastUpdateRequest!.ReleaseId); Assert.False(f.Vm.IsDatapackUpdateOpen); Assert.Contains("备份", f.Vm.DatapackManagementFeedback); Assert.Equal(original, File.ReadAllBytes(f.LevelOne)); Assert.False(f.Root.ShowOperationStatus);
    }
    [AvaloniaFact]
    public async Task CancelledLateOrderReadCannotRepopulateDiscardedDraftOrShowBottomBar()
    {
        using var f = new Fixture(); PrepareSortablePacks(f); await OpenPackManagement(f); var state = await f.Service.Store("one").GetDatapackOrderAsync("world");
        var delayed = new TaskCompletionSource<WorldDatapackOrderState>(); f.Service.OrderRead = (_, _, _) => delayed.Task;
        var pending = f.Vm.ReadDatapackOrderCommand.ExecuteAsync(null); Assert.True(f.Root.IsBusy); Assert.False(f.Root.ShowOperationStatus);
        Click(f.Window, "CancelDatapackManagementButton"); delayed.SetResult(state); await pending; Pump(f.Window);
        Assert.Empty(f.Vm.DatapackOrderItems); Assert.False(f.Vm.IsDatapackOrderOpen); Assert.False(f.Vm.IsDatapackManagementBusy); Assert.Equal(0, f.Service.OrderSaves);
    }
    [AvaloniaFact]
    public async Task ChangedTargetDiscardsLateUpdatePreviewAndCapturedLocalSelection()
    {
        using var f = new Fixture(); await OpenPackManagement(f); await f.Vm.ReadDatapackUpdatesCommand.ExecuteAsync(null); var context = f.Vm.DatapackUpdateContext!; var pack = f.Vm.DatapackUpdatePackId!;
        await f.Vm.PreviewLocalDatapackUpdateAsync("fixture.zip", context + "changed", pack); Assert.Equal(0, f.Service.UpdatePreviews);
        var delayed = new TaskCompletionSource<WorldDatapackUpdatePreview>(); f.Service.UpdatePreview = (_, _) => delayed.Task;
        var pending = f.Vm.PreviewDatapackUpdateCommand.ExecuteAsync(null); f.Vm.SelectedDatapacks.Clear(); delayed.SetResult(new("late", "One", "world", ["replace"], [])); await pending; Pump(f.Window);
        Assert.False(f.Vm.HasDatapackUpdatePreview); Assert.Contains("late", f.Service.DiscardedUpdates); Assert.Equal(0, f.Service.UpdateApplies);
    }
    [AvaloniaFact]
    public async Task ManualReplacementIsAvailableForUntrackedPackAndFailureKeepsOriginal()
    {
        using var f = new Fixture(); await OpenPackManagement(f); f.Service.ManualOnly = true; var original = File.ReadAllBytes(f.LevelOne);
        await f.Vm.ReadDatapackUpdatesCommand.ExecuteAsync(null); Pump(f.Window); Assert.False(Find<ComboBox>(f.Window, "DatapackUpdateVersionSelector").IsEffectivelyVisible); Assert.True(f.Vm.CanChooseLocalDatapackUpdate);
        await f.Vm.PreviewLocalDatapackUpdateAsync("manual.zip", f.Vm.DatapackUpdateContext!, f.Vm.DatapackUpdatePackId!); Assert.Equal("manual.zip", f.Service.LastUpdateRequest!.LocalArchivePath); Assert.Null(f.Service.LastUpdateRequest.ReleaseId);
        f.Service.UpdateApply = (_, _) => throw new IOException("controlled failure"); await f.Vm.ConfirmDatapackUpdateCommand.ExecuteAsync(null);
        Assert.True(f.Root.HasError); Assert.False(f.Vm.HasDatapackUpdatePreview); Assert.Equal(original, File.ReadAllBytes(f.LevelOne));
    }
    [AvaloniaFact]
    public async Task DatapackManagementCardsAndVersionSelectionHaveNoBindingWarnings()
    {
        var previous = Logger.Sink; var failures = new BindingFailures(); Logger.Sink = failures;
        try
        {
            using var f = new Fixture(); PrepareSortablePacks(f); await OpenPackManagement(f); await f.Vm.ReadDatapackOrderCommand.ExecuteAsync(null); Pump(f.Window);
            await f.Vm.ReadDatapackUpdatesCommand.ExecuteAsync(null); Pump(f.Window); await f.Vm.PreviewDatapackUpdateCommand.ExecuteAsync(null); Pump(f.Window); f.Vm.CancelDatapackManagementCommand.Execute(null); Pump(f.Window); Assert.Empty(failures.Errors);
        }
        finally { Logger.Sink = previous; }
    }
    private sealed partial class WorldEditUiService
    {
        public int OrderSaves, UpdatePreviews, UpdateApplies; public string? LastOrderBackup; public bool ManualOnly;
        public WorldDatapackUpdateRequest? LastUpdateRequest; public List<string> DiscardedUpdates { get; } = [];
        public Func<string, string, CancellationToken, Task<WorldDatapackOrderState>>? OrderRead;
        public Func<WorldDatapackUpdateRequest, CancellationToken, Task<WorldDatapackUpdatePreview>>? UpdatePreview;
        public Func<string, CancellationToken, Task<WorldDatapackUpdateReceipt>>? UpdateApply;
        public Task<WorldDatapackOrderState> GetWorldDatapackOrderAsync(string id, string world, CancellationToken ct = default) => OrderRead?.Invoke(id, world, ct) ?? Store(id).GetDatapackOrderAsync(world, ct);
        public async Task<WorldEditReceipt> SaveWorldDatapackOrderAsync(string id, string world, string revision, IReadOnlyList<WorldDatapackOrderItem> order, CancellationToken ct = default)
        { OrderSaves++; var receipt = await Store(id).SaveDatapackOrderAsync(world, revision, order, ct); LastOrderBackup = receipt.BackupPath; return receipt; }
        public async Task<WorldDatapackUpdateOptions> GetWorldDatapackUpdateOptionsAsync(string id, string world, string packId, CancellationToken ct = default)
        {
            var state = await Store(id).GetEditStateAsync(world, ct); var pack = state.Datapacks.Single(p => p.PackId == packId);
            return new(state.Revision, new(packId, pack.ContentRevision), pack.File.Name, ManualOnly ? "本地 ZIP" : "Modrinth", ManualOnly ? null : "fixture", ManualOnly ? null : "old", ManualOnly ? [] : [new("new", "2", "New", "new.zip", DateTimeOffset.UtcNow), new("older", "1.5", "Older", "older.zip", DateTimeOffset.UtcNow.AddDays(-1))]);
        }
        public Task<WorldDatapackUpdatePreview> PreviewWorldDatapackUpdateAsync(WorldDatapackUpdateRequest request, CancellationToken ct = default)
        { UpdatePreviews++; LastUpdateRequest = request; return UpdatePreview?.Invoke(request, ct) ?? Task.FromResult(new WorldDatapackUpdatePreview("update-" + UpdatePreviews, "One", "world", ["Replace fixture"], [])); }
        public Task<WorldDatapackUpdateReceipt> ApplyWorldDatapackUpdateAsync(string id, CancellationToken ct = default)
        { UpdateApplies++; return UpdateApply?.Invoke(id, ct) ?? Task.FromResult(new WorldDatapackUpdateReceipt("updated", "controlled-backup")); }
        public Task DiscardWorldDatapackUpdateAsync(string id, CancellationToken ct = default) { DiscardedUpdates.Add(id); return Task.CompletedTask; }
    }
}
