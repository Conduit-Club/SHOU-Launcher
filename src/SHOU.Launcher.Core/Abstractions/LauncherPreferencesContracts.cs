namespace SHOU.Launcher.Core.Abstractions;

public sealed record LauncherBehavior(string RegionalFormat = "system", int LogLineLimit = 2000,
    bool DisableHardwareAcceleration = false, bool UseDnsOverHttps = false, bool CheckUpdatesOnStartup = false);
public sealed record LauncherRelease(string Tag, string Title, string Notes, DateTimeOffset Published,
    Uri Page, bool Prerelease, LauncherUpdateAsset? Package);
public sealed record LauncherUpdateAsset(string Name, Uri Download, long Bytes, string Sha256);
public sealed record LauncherFeedback(string Title, string State, Uri Page, string Body);
public interface IJavaManagementService
{
    Task<JavaInstallation> InspectJavaAsync(string executable, CancellationToken cancellationToken = default);
}
public interface ILauncherInformationService
{
    Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LauncherFeedback>> GetLauncherFeedbackAsync(CancellationToken cancellationToken = default);
    Task<string> DownloadLauncherUpdateAsync(LauncherUpdateAsset asset, CancellationToken cancellationToken = default);
    Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken cancellationToken = default);
}
