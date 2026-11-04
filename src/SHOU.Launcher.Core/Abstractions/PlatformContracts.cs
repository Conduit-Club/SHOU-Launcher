namespace SHOU.Launcher.Core.Abstractions;

public enum LauncherOS { Unknown, Windows, MacOS, Linux }
public enum RuntimeArchitecture { Unknown, X86, X64, Arm64 }
public enum ProxyMode { Direct, System, Custom }
public sealed record PlatformContext(LauncherOS OS, RuntimeArchitecture HardwareArchitecture,
    RuntimeArchitecture ProcessArchitecture, string OSVersion)
{
    public string MinecraftOS => OS switch { LauncherOS.Windows => "windows", LauncherOS.MacOS => "osx", LauncherOS.Linux => "linux", _ => "unknown" };
    public string RuntimeIdentifier => (OS switch { LauncherOS.Windows => "win", LauncherOS.MacOS => "osx", LauncherOS.Linux => "linux", _ => "unknown" })
        + "-" + ProcessArchitecture.ToString().ToLowerInvariant();
}
public interface IPlatformInfo { PlatformContext Context { get; } }
public interface IAppPaths
{
    string DataDirectory { get; }
    string SharedDataDirectory { get; }
    string LocalDataDirectory { get; }
    string CacheDirectory { get; }
    string LogsDirectory { get; }
    string RuntimesDirectory { get; }
    string InstancesDirectory { get; }
    string TempDirectory { get; }
    string ResourcesDirectory { get; }
    string LockFile { get; }
    string CredentialScope { get; }
    bool IsIsolated { get; }
}
public interface IPlatformCapabilities
{
    bool SupportsCredentialPersistence { get; }
    bool SupportsSystemProxy { get; }
    bool SupportsRevealFile { get; }
    IReadOnlyList<string> Limitations { get; }
}
public interface IExternalLauncher
{
    Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);
    Task OpenDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task RevealFileAsync(string file, CancellationToken cancellationToken = default);
}
public enum CredentialFailure { Locked, Denied, Unavailable, Corrupt, IOFailure }
public sealed class CredentialStoreException(CredentialFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{ public CredentialFailure Failure { get; } = failure; }
public interface ICredentialStore
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null, TimeSpan? Timeout = null,
    int OutputLimitCharacters = 1048576,
    [property: System.Text.Json.Serialization.JsonIgnore] SensitiveProcessInput? SensitiveInput = null)
{
    public override string ToString() => "ProcessSpec [arguments/environment redacted; sensitive stdin=" + (SensitiveInput is not null) + "]";
}
public sealed record ProcessOutput(DateTimeOffset Timestamp, bool IsStandardError, string Text);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError,
    bool TimedOut = false, bool Cancelled = false, bool OutputTruncated = false);
public interface IManagedProcess : IAsyncDisposable
{
    int ProcessId { get; }
    Task<ProcessResult> Completion { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}
public interface IProcessRunner
{
    Task<IManagedProcess> StartAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<ProcessOutput>? output = null, CancellationToken cancellationToken = default);
}
public interface IDataDirectoryLease : IDisposable { string LockPath { get; } }
public interface IDataDirectoryLock { IDataDirectoryLease Acquire(IAppPaths paths); }
public sealed record PlatformOptions(string? DataDirectory = null, string? ConfiguredJavaExecutable = null);
public sealed record PlatformServices(IPlatformInfo Info, IAppPaths Paths, IPlatformCapabilities Capabilities,
    IExternalLauncher ExternalLauncher, ICredentialStore Credentials, IProcessRunner Processes,
    IJavaDiscovery JavaDiscovery, IJavaRuntimeInstaller JavaInstaller, IDataDirectoryLock DataLock);

public sealed record JavaInstallation(string ExecutablePath, string JavaHome, string FullVersion, int MajorVersion,
    string Vendor, RuntimeArchitecture RuntimeArchitecture, string Source, string? ProbeError = null,
    string? ValidatedManifestSha1 = null)
{ public bool IsUsable => ProbeError is null && MajorVersion > 0 && RuntimeArchitecture != RuntimeArchitecture.Unknown; }
public sealed record JavaDiscoveryRequest(string? ConfiguredExecutable, string ManagedRuntimeDirectory,
    int? RequiredMajorVersion = null, RuntimeArchitecture? RequiredArchitecture = null);
public interface IJavaDiscovery
{
    Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(JavaDiscoveryRequest request, CancellationToken cancellationToken = default);
}
public sealed record JavaRuntimeRequest(string Component, int MajorVersion, LauncherOS OS,
    RuntimeArchitecture Architecture, string InstallRoot, Uri? CatalogUri = null);
public sealed record TransferProgress(string Id, string Stage, long BytesReceived, long? TotalBytes, string? Source = null);
public interface IJavaRuntimeInstaller
{
    Task<JavaInstallation> InstallAsync(JavaRuntimeRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record DownloadRequest(IReadOnlyList<Uri> Sources, string DestinationPath,
    string? Sha1 = null, string? Sha256 = null, long? Size = null, bool AllowResume = true);
public interface IVerifiedDownloader
{
    Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}
