namespace SHOU.Launcher.Core.Abstractions;

public sealed record InstanceExportEntry(string RelativePath, bool Directory, long Bytes, bool CanSelect = true, string? Reason = null)
{
    public string Name => RelativePath.Split('/')[^1];
}
public interface IInstanceExportSelectionService
{
    Task<IReadOnlyList<InstanceExportEntry>> GetInstanceExportEntriesAsync(string instanceId, string directory = "", CancellationToken cancellationToken = default);
}
