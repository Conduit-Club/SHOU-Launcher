namespace SHOU.Launcher.Core.Abstractions;

public enum InstanceIsolation { All, Modded, Shared }
public enum LauncherVisibility { Keep, Minimize, Hide, Close }
public enum GameProcessPriority { Normal, BelowNormal, AboveNormal, High }
public enum GameIpPreference { JavaDefault, IPv4, IPv6 }
public enum DownloadSourcePreference { Custom, OfficialFirst, MirrorFirst }
public sealed record GameLaunchOptions(bool AutomaticMemory = false, InstanceIsolation Isolation = InstanceIsolation.All,
    LauncherVisibility Visibility = LauncherVisibility.Keep, GameProcessPriority Priority = GameProcessPriority.Normal,
    GameIpPreference IpPreference = GameIpPreference.JavaDefault, string WindowTitle = "", string JvmArguments = "",
    bool LockMemory = false, int? WindowWidth = null, int? WindowHeight = null, bool? Fullscreen = null,
    int? MinimumMemoryMiB = null, string? CustomInfo = null, string GameArguments = "",
    string BeforeLaunchExecutable = "", string BeforeLaunchArguments = "", bool InheritGlobal = false);
public sealed record DownloadBehavior(int MaximumConnections = 8, int MaximumKiBPerSecond = 0,
    bool HttpsOnly = false, DownloadSourcePreference SourcePreference = DownloadSourcePreference.Custom,
    bool PreferOfficialCatalog = true, bool SelectInstalledInstance = true);
public sealed record MemoryStatus(long TotalBytes, long AvailableBytes);
public interface IMemoryInfo { MemoryStatus Read(); }
public interface IGameSettingsService { MemoryStatus? ReadMemoryStatus(); }
public interface IGameWindowCustomization { Task SetGameWindowTitleAsync(IManagedProcess process, string title, CancellationToken cancellationToken); }
public sealed record ExternalInstanceOrigin(string MinecraftRoot, string VersionDirectory, string OriginalVersionId, string MetadataSha256);
public sealed record ExistingInstanceCandidate(string Name, string VersionId, string GameVersion, string Loader,
    string? LoaderVersion, string GameDirectory, ExternalInstanceOrigin Origin, InstancePresentation? Presentation = null);
public sealed record ExistingInstanceIssue(string Name, string Reason);
public sealed record ExistingInstanceInspection(IReadOnlyList<ExistingInstanceCandidate> Candidates, IReadOnlyList<ExistingInstanceIssue> Issues);
public interface IExistingInstanceBatchService
{
    Task<ExistingInstanceInspection> InspectInstanceFolderDetailedAsync(string folder, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameInstance>> AttachExistingInstancesAsync(IReadOnlyList<ExistingInstanceCandidate> candidates, CancellationToken cancellationToken = default);
}
public interface IExistingInstanceService
{
    Task<IReadOnlyList<ExistingInstanceCandidate>> InspectInstanceFolderAsync(string folder, CancellationToken cancellationToken = default);
    Task<GameInstance> AttachExistingInstanceAsync(ExistingInstanceCandidate candidate, CancellationToken cancellationToken = default);
}
