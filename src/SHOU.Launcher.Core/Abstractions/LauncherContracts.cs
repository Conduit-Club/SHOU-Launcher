namespace SHOU.Launcher.Core.Abstractions;

public interface IBuildConfiguration
{
    string? MicrosoftClientId { get; }
    string? CurseForgeApiKey { get; }
    string SourceSha { get; }
    string Configuration { get; }
    string OwnerAssembly { get; }
}
public sealed record BuildIdentity(string Name, string Version, string Codename, string SourceSha,
    string Configuration, string OwnerAssembly, bool MicrosoftConfigured, bool CurseForgeConfigured);
public sealed record ProxySettings(ProxyMode Mode = ProxyMode.System, string? Address = null, string? CredentialKey = null);
public sealed record Aria2Settings(bool Enabled = false, string Endpoint = "http://localhost:6800/jsonrpc", string? CredentialKey = null);
public sealed record LauncherSettings(string? GameRoot = null, string? JavaExecutable = null, int MemoryMiB = 2048,
    string Language = "zh-CN", string Theme = "System", string DownloadSourcesJson = "",
    ProxySettings? Proxy = null, Aria2Settings? Aria2 = null, string? MicrosoftClientId = null);
public sealed record GameInstance(string Id, string Name, string VersionId, string GameDirectory,
    string? JavaExecutable = null, int? MemoryMiB = null, string Loader = "vanilla", string? LoaderVersion = null,
    bool Installed = false);
public enum AccountKind { Offline, Microsoft, Yggdrasil }
public sealed record AccountProfile(string Id, AccountKind Kind, string DisplayName, string PlayerUuid,
    string? AuthenticationServer = null, bool Persisted = false, string Status = "本地档案",
    DateTimeOffset? ExpiresAt = null);
public sealed record ClubServer(string Id, string Name, string Address, string Status = "尚未查询", int? OnlinePlayers = null);
public sealed record DownloadTaskSnapshot(string Id, string Name, string State, long BytesReceived = 0,
    long? TotalBytes = null, string? Source = null, string? Error = null, double BytesPerSecond = 0);
public sealed record LauncherLog(DateTimeOffset Timestamp, string Level, string Message);
public sealed record DeviceCodeChallenge(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, string Message);
public sealed record LauncherSnapshot(BuildIdentity Build, PlatformContext Platform, LauncherSettings Settings,
    IReadOnlyList<GameInstance> Instances, IReadOnlyList<AccountProfile> Accounts,
    IReadOnlyList<JavaInstallation> JavaInstallations, IReadOnlyList<ClubServer> Servers,
    IReadOnlyList<DownloadTaskSnapshot> Downloads, string? SelectedAccountId = null,
    string? SelectedInstanceId = null, string State = "Ready", string? Error = null,
    int? GameProcessId = null, bool CanPersistAccounts = true);
public sealed record LaunchPlan(string JavaExecutable, RuntimeArchitecture JavaRuntimeArchitecture,
    string WorkingDirectory, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides, IReadOnlyList<string> ClasspathEntries,
    IReadOnlyList<string> ResolvedLibraries, IReadOnlyList<string> LegacyNativeExtractions,
    IReadOnlyList<string> RedactedDiagnostics)
{
    // Launch arguments contain runtime credentials. Only this explicitly redacted view is reportable.
    public object ToRedactedReport() => new { JavaExecutable, JavaRuntimeArchitecture, WorkingDirectory,
        ClasspathEntries, ResolvedLibraries, LegacyNativeExtractions, RedactedDiagnostics };
}
public interface ILauncherService : IAsyncDisposable
{
    LauncherSnapshot Snapshot { get; }
    IAppPaths Paths { get; }
    IPlatformCapabilities Capabilities { get; }
    event EventHandler<LauncherSnapshot>? SnapshotChanged;
    event EventHandler<LauncherLog>? LogReceived;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<GameInstance> CreateInstanceAsync(string name, string versionId, CancellationToken cancellationToken = default);
    Task SelectInstanceAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken cancellationToken = default);
    Task InstallInstanceAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<LaunchPlan> BuildLaunchPlanAsync(string instanceId, CancellationToken cancellationToken = default);
    Task LaunchAsync(string instanceId, string serverId = "none", CancellationToken cancellationToken = default);
    Task StopGameAsync(CancellationToken cancellationToken = default);
    Task CancelDownloadAsync(string taskId, CancellationToken cancellationToken = default);
    Task RetryDownloadAsync(string taskId, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
    Task<AccountProfile> AddOfflineAccountAsync(string playerName, CancellationToken cancellationToken = default);
    Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string username, string password, CancellationToken cancellationToken = default);
    Task RefreshAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task SelectAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task RefreshServersAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<LauncherLog> GetRecentLogs();
}
