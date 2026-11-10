namespace SHOU.Launcher.Core.Abstractions;

public sealed record WorldDatapackOrderItem(string PackId, string Name, string ContentRevision);
public sealed record WorldDatapackOrderState(string Revision, IReadOnlyList<WorldDatapackOrderItem> Items, IReadOnlyList<string> Warnings);
public sealed record WorldDatapackUpdateChoice(string ReleaseId, string Version, string Name, string FileName, DateTimeOffset Published)
{
    public string Label => $"{Version} · {FileName} · {Published.ToLocalTime():g}";
}
public sealed record WorldDatapackUpdateOptions(string WorkspaceRevision, WorldDatapackSelection Selection, string Name,
    string Origin, string? ProjectId, string? CurrentReleaseId, IReadOnlyList<WorldDatapackUpdateChoice> Choices, string? Notice = null);
public sealed record WorldDatapackUpdateRequest(string InstanceId, string WorldDirectoryName, string WorkspaceRevision,
    WorldDatapackSelection Selection, string? ReleaseId = null, string? FileName = null, string? LocalArchivePath = null);
public sealed record WorldDatapackUpdatePreview(string Id, string InstanceName, string WorldDirectoryName,
    IReadOnlyList<string> Changes, IReadOnlyList<string> Warnings);
public sealed record WorldDatapackUpdateReceipt(string Revision, string BackupDirectory);
public interface IWorldDatapackManagementService
{
    Task<WorldDatapackOrderState> GetWorldDatapackOrderAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default);
    Task<WorldEditReceipt> SaveWorldDatapackOrderAsync(string instanceId, string worldDirectoryName, string expectedRevision,
        IReadOnlyList<WorldDatapackOrderItem> order, CancellationToken cancellationToken = default);
    Task<WorldDatapackUpdateOptions> GetWorldDatapackUpdateOptionsAsync(string instanceId, string worldDirectoryName, string packId, CancellationToken cancellationToken = default);
    Task<WorldDatapackUpdatePreview> PreviewWorldDatapackUpdateAsync(WorldDatapackUpdateRequest request, CancellationToken cancellationToken = default);
    Task<WorldDatapackUpdateReceipt> ApplyWorldDatapackUpdateAsync(string previewId, CancellationToken cancellationToken = default);
    Task DiscardWorldDatapackUpdateAsync(string previewId, CancellationToken cancellationToken = default);
}
