using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using SHOU.Launcher.Core.Abstractions;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed partial class WpfWorldEditingUiTests
{
    [AvaloniaFact]
    public async Task BackupControlsRequirePreviewAndExplicitConfirmationAndRefreshAfterRestore()
    {
        using var f = new Fixture(); await OpenPackManagement(f); var original = File.ReadAllBytes(f.LevelOne);
        Click(f.Window, "ReadDatapackBackupsButton"); await f.Vm.ReadDatapackBackupsCommand.ExecutionTask!; Pump(f.Window);
        Assert.True(Find<Border>(f.Window, "DatapackBackupsPanel").IsEffectivelyVisible); Assert.Equal(2, f.Vm.DatapackBackups.Count); Assert.False(f.Vm.ConfirmDatapackRestoreCommand.CanExecute(null));
        Click(f.Window, "PreviewDatapackRestoreButton"); await f.Vm.PreviewDatapackRestoreCommand.ExecutionTask!; Pump(f.Window);
        Assert.True(f.Vm.HasDatapackRestorePreview); Assert.Equal(0, f.Service.BackupRestores); Assert.Equal(original, File.ReadAllBytes(f.LevelOne));
        Click(f.Window, "ConfirmDatapackRestoreButton"); await f.Vm.ConfirmDatapackRestoreCommand.ExecutionTask!; Pump(f.Window);
        Assert.Equal(1, f.Service.BackupRestores); Assert.False(f.Vm.HasDatapackRestorePreview); Assert.False(f.Vm.SelectedDatapackBackup!.CanRestore); Assert.Contains("已恢复", f.Vm.DatapackManagementFeedback); Assert.False(f.Root.ShowOperationStatus);
    }
    [AvaloniaFact]
    public async Task BackupSelectionChangeInvalidatesPreviewAndOldRecordsCanOnlyOpenTheirFolder()
    {
        using var f = new Fixture(); await OpenPackManagement(f); await f.Vm.ReadDatapackBackupsCommand.ExecuteAsync(null); await f.Vm.PreviewDatapackRestoreCommand.ExecuteAsync(null);
        Find<ComboBox>(f.Window, "DatapackBackupSelector").SelectedItem = f.Vm.DatapackBackups[1]; Pump(f.Window);
        Assert.False(f.Vm.HasDatapackRestorePreview); Assert.Contains("restore-1", f.Service.DiscardedRestores); Assert.False(f.Vm.PreviewDatapackRestoreCommand.CanExecute(null)); Assert.False(f.Vm.ConfirmDatapackRestoreCommand.CanExecute(null));
        Assert.True(f.Vm.OpenDatapackBackupDirectoryCommand.CanExecute(null)); await f.Vm.OpenDatapackBackupDirectoryCommand.ExecuteAsync(null); Assert.Equal(f.Vm.SelectedDatapackBackup!.Id, f.Service.OpenedBackupId);
    }
    [AvaloniaFact]
    public async Task CancelledLateBackupListingCannotReopenPanelAndHasNoBottomProgressBar()
    {
        using var f = new Fixture(); await OpenPackManagement(f); var delayed = new TaskCompletionSource<IReadOnlyList<WorldDatapackBackupEntry>>(); f.Service.BackupRead = (_, _, _) => delayed.Task;
        var pending = f.Vm.ReadDatapackBackupsCommand.ExecuteAsync(null); Assert.True(f.Root.IsBusy); Assert.False(f.Root.ShowOperationStatus);
        Click(f.Window, "CancelDatapackManagementButton"); delayed.SetResult(f.Service.BackupEntries()); await pending; Pump(f.Window);
        Assert.False(f.Vm.IsDatapackBackupsOpen); Assert.Empty(f.Vm.DatapackBackups); Assert.Equal(0, f.Service.BackupRestores);
    }
    [AvaloniaFact]
    public async Task LateRestorePreviewForPreviousInstanceIsDiscarded()
    {
        using var f = new Fixture(); await OpenPackManagement(f); await f.Vm.ReadDatapackBackupsCommand.ExecuteAsync(null); var delayed = new TaskCompletionSource<WorldDatapackRestorePreview>(); f.Service.BackupPreview = (_, _, _, _) => delayed.Task;
        var pending = f.Vm.PreviewDatapackRestoreCommand.ExecuteAsync(null); f.Service.Base.Publish(f.Service.Snapshot with { SelectedInstanceId = "two" });
        delayed.SetResult(new("late-restore", new string('a', 32), ["restore fixture"], [])); await pending; Pump(f.Window);
        Assert.False(f.Vm.HasDatapackRestorePreview); Assert.Contains("late-restore", f.Service.DiscardedRestores); Assert.Equal(0, f.Service.BackupRestores);
    }
    [AvaloniaFact]
    public async Task FailedRestoreDoesNotClaimSuccessOrKeepReusableConfirmation()
    {
        using var f = new Fixture(); await OpenPackManagement(f); await f.Vm.ReadDatapackBackupsCommand.ExecuteAsync(null); await f.Vm.PreviewDatapackRestoreCommand.ExecuteAsync(null); var original = File.ReadAllBytes(f.LevelOne);
        f.Service.BackupRestore = (_, _) => throw new IOException("controlled backup conflict"); await f.Vm.ConfirmDatapackRestoreCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.True(f.Root.HasError); Assert.False(f.Vm.HasDatapackRestorePreview); Assert.Contains("controlled backup conflict", f.Vm.DatapackManagementFeedback); Assert.Equal(original, File.ReadAllBytes(f.LevelOne)); Assert.False(f.Root.ShowOperationStatus);
    }
    [AvaloniaFact]
    public async Task DirtyOrderInvalidatesRestorePreviewAndBackupCardsHaveNoBindingWarnings()
    {
        var previous = Logger.Sink; var failures = new BindingFailures(); Logger.Sink = failures;
        try
        {
            using var f = new Fixture(); PrepareSortablePacks(f); await OpenPackManagement(f); await f.Vm.ReadDatapackOrderCommand.ExecuteAsync(null); await f.Vm.ReadDatapackBackupsCommand.ExecuteAsync(null); await f.Vm.PreviewDatapackRestoreCommand.ExecuteAsync(null); Pump(f.Window);
            f.Vm.SelectedDatapackOrderItem = f.Vm.DatapackOrderItems[1]; f.Vm.MoveDatapackOrderUpCommand.Execute(null); Pump(f.Window);
            Assert.True(f.Vm.IsDatapackOrderDirty); Assert.False(f.Vm.HasDatapackRestorePreview); Assert.False(f.Vm.ReadDatapackBackupsCommand.CanExecute(null)); Assert.False(f.Vm.ConfirmDatapackRestoreCommand.CanExecute(null)); Assert.Empty(failures.Errors);
        }
        finally { Logger.Sink = previous; }
    }
    private sealed partial class WorldEditUiService : IWorldDatapackBackupService
    {
        public int BackupRestores, BackupPreviews; public bool BackupRestored; public string? OpenedBackupId;
        public List<string> DiscardedRestores { get; } = [];
        public Func<string, string, CancellationToken, Task<IReadOnlyList<WorldDatapackBackupEntry>>>? BackupRead;
        public Func<string, string, string, CancellationToken, Task<WorldDatapackRestorePreview>>? BackupPreview;
        public Func<string, CancellationToken, Task<WorldDatapackRestoreReceipt>>? BackupRestore;
        public IReadOnlyList<WorldDatapackBackupEntry> BackupEntries() => [new(new string('a', 32), DateTimeOffset.UtcNow, ["fixture.zip"], !BackupRestored, BackupRestored ? "已恢复" : "可检查恢复"), new(new string('b', 32), DateTimeOffset.MinValue, ["legacy.zip"], false, "旧版备份仅供手动核对")];
        public Task<IReadOnlyList<WorldDatapackBackupEntry>> GetWorldDatapackBackupsAsync(string id, string world, CancellationToken ct = default) => BackupRead?.Invoke(id, world, ct) ?? Task.FromResult(BackupEntries());
        public Task<string> GetWorldDatapackBackupDirectoryAsync(string id, string world, string backupId, CancellationToken ct = default) { OpenedBackupId = backupId; return Task.FromResult(Paths.TempDirectory); }
        public Task<WorldDatapackRestorePreview> PreviewWorldDatapackRestoreAsync(string id, string world, string backupId, CancellationToken ct = default)
        { BackupPreviews++; return BackupPreview?.Invoke(id, world, backupId, ct) ?? Task.FromResult(new WorldDatapackRestorePreview("restore-" + BackupPreviews, backupId, ["Restore fixture"], ["Keep both versions"])); }
        public Task<WorldDatapackRestoreReceipt> RestoreWorldDatapackBackupAsync(string id, CancellationToken ct = default)
        { BackupRestores++; if (BackupRestore is not null) return BackupRestore(id, ct); BackupRestored = true; return Task.FromResult(new WorldDatapackRestoreReceipt("controlled-original", "controlled-current")); }
        public Task DiscardWorldDatapackRestoreAsync(string id, CancellationToken ct = default) { DiscardedRestores.Add(id); return Task.CompletedTask; }
    }
}
