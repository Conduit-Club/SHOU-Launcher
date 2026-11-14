using System.Text.Json.Serialization;

namespace SHOU.Launcher.Core.Abstractions;

public sealed record LegacyMicrosoftAccountChoice(string SourceId, string DisplayName);

/// <summary>Transient platform-to-Core handoff. Never a persisted DTO or a view model.</summary>
public sealed class LegacyMicrosoftAccountCredential(string sourceId, string displayName, string playerUuid, string refreshToken)
{
    public string SourceId { get; } = sourceId;
    public string DisplayName { get; } = displayName;
    public string PlayerUuid { get; } = playerUuid;
    [JsonIgnore] internal string RefreshToken { get; } = refreshToken;
    public override string ToString() => "LegacyMicrosoftAccountCredential [redacted]";
}

public interface ILegacyMicrosoftAccountReader
{
    Task<IReadOnlyList<LegacyMicrosoftAccountCredential>> ReadAsync(CancellationToken cancellationToken = default);
}

public interface ILegacyMicrosoftAccountMigrationService
{
    bool SupportsLegacyMicrosoftImport { get; }
    Task<IReadOnlyList<LegacyMicrosoftAccountChoice>> ReadLegacyMicrosoftAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountProfile> ImportLegacyMicrosoftAccountAsync(string sourceId, CancellationToken cancellationToken = default);
}
