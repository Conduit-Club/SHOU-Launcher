namespace SHOU.Launcher.Core.Abstractions;

public sealed record WorldDatapackBackupEntry(string Id, DateTimeOffset Created, IReadOnlyList<string> Names, bool CanRestore, string Status)
{
    public string Label => (Created == DateTimeOffset.MinValue ? "较早的备份" : Created.ToLocalTime().ToString("g")) + " · " + string.Join("、", Names);
}
public sealed record WorldDatapackRestorePreview(string Id, string BackupId, IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings);
public sealed record WorldDatapackRestoreReceipt(string BackupDirectory, string PreservedCurrentDirectory);
public interface IWorldDatapackBackupService
{
    Task<IReadOnlyList<WorldDatapackBackupEntry>> GetWorldDatapackBackupsAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default);
    Task<string> GetWorldDatapackBackupDirectoryAsync(string instanceId, string worldDirectoryName, string backupId, CancellationToken cancellationToken = default);
    Task<WorldDatapackRestorePreview> PreviewWorldDatapackRestoreAsync(string instanceId, string worldDirectoryName, string backupId, CancellationToken cancellationToken = default);
    Task<WorldDatapackRestoreReceipt> RestoreWorldDatapackBackupAsync(string previewId, CancellationToken cancellationToken = default);
    Task DiscardWorldDatapackRestoreAsync(string previewId, CancellationToken cancellationToken = default);
}
