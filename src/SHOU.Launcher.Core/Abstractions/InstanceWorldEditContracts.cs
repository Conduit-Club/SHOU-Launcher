namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Explicit, stopped-game edits for a selected isolated Java world. Separate from the read/import contract.</summary>
public interface IInstanceWorldEditService
{
    Task<WorldEditState> GetWorldEditStateAsync(string instanceId, string worldDirectoryName, CancellationToken cancellationToken = default);
    Task<WorldEditReceipt> SaveWorldMetadataAsync(string instanceId, string worldDirectoryName, string expectedRevision,
        WorldMetadataEdit edit, CancellationToken cancellationToken = default);
    Task<WorldEditReceipt> SetWorldDatapacksEnabledAsync(string instanceId, string worldDirectoryName, string expectedRevision,
        IReadOnlyList<WorldDatapackSelection> packs, bool enabled, CancellationToken cancellationToken = default);
}
public sealed record WorldMetadataValues(bool? AllowCommands, int? Difficulty, bool? DifficultyLocked,
    int? SpawnX, int? SpawnY, int? SpawnZ, long? WorldTicks);
public sealed record WorldMetadataEdit(bool AllowCommands, int Difficulty, bool DifficultyLocked);
public sealed record WorldDatapackState(WorldDatapackFile File, string PackId, string ContentRevision, bool? Enabled,
    string Description, int? PackFormat, bool CanToggle, string Capability);
public sealed record WorldDatapackSelection(string PackId, string ContentRevision);
public sealed record WorldEditState(string Revision, int? DataVersion, string? Version, WorldMetadataValues Values,
    IReadOnlyList<WorldDatapackState> Datapacks, bool CanEditMetadata, bool CanToggleDatapacks, string Capability);
public sealed record WorldEditReceipt(string Revision, string? BackupPath);
