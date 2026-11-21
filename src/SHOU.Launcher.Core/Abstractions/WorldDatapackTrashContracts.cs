namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Reversible, explicit local pack removal. No game process or credential access.</summary>
public interface IWorldDatapackTrashService
{
    Task<IReadOnlyList<WorldDatapackTrashEntry>> GetWorldDatapackTrashAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default);
    Task RecycleWorldDatapacksAsync(string instanceId, string worldDirectoryName, string expectedRevision,
        IReadOnlyList<WorldDatapackSelection> packs, CancellationToken cancellationToken = default);
    Task RestoreWorldDatapacksAsync(string instanceId, string worldDirectoryName, string trashId, CancellationToken cancellationToken = default);
}

public sealed record WorldDatapackTrashEntry(string Id, IReadOnlyList<string> Names, DateTimeOffset Created, bool CanRestore, string Status)
{
    public string Label => string.Join("、", Names) + " · " + Created.ToLocalTime().ToString("g");
}
