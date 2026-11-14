using System.Text.Json.Serialization;
using SHOU.Launcher.Core.Authentication;

namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Local, transient handoff only. Never serialize the original credential container.</summary>
public sealed class LegacyDesktopData(IReadOnlyList<string> gameFolders, IReadOnlyList<AuthSession> accounts,
    string? selectedAccountId = null, string? selectedVersionDirectory = null, IReadOnlyList<string>? issues = null,
    IReadOnlyDictionary<string, string>? launchSettings = null, string? executableDirectory = null)
{
    public IReadOnlyList<string> GameFolders { get; } = gameFolders;
    [JsonIgnore] public IReadOnlyList<AuthSession> Accounts { get; } = accounts;
    public string? SelectedAccountId { get; } = selectedAccountId;
    public string? SelectedVersionDirectory { get; } = selectedVersionDirectory;
    public IReadOnlyList<string> Issues { get; } = issues ?? [];
    public IReadOnlyDictionary<string, string> LaunchSettings { get; } = launchSettings ?? new Dictionary<string, string>();
    public string? ExecutableDirectory { get; } = executableDirectory;
    public override string ToString() => "LegacyDesktopData [private local handoff]";
}

public interface ILegacyDesktopDataReader
{
    Task<LegacyDesktopData> ReadAsync(CancellationToken cancellationToken = default);
}
