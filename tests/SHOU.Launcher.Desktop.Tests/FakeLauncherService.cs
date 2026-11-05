using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Tests;

// Explicit test fake. No production composition root references this implementation.
internal sealed class FakeLauncherService : ILauncherService
{
    private EventHandler<LauncherSnapshot>? _snapshotChanged;
    private EventHandler<LauncherLog>? _logReceived;
    public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => _snapshotChanged += value; remove => _snapshotChanged -= value; }
    public event EventHandler<LauncherLog>? LogReceived { add => _logReceived += value; remove => _logReceived -= value; }
    public int SnapshotSubscribers => _snapshotChanged?.GetInvocationList().Length ?? 0;
    public int LogSubscribers => _logReceived?.GetInvocationList().Length ?? 0;
    public LauncherSnapshot Snapshot { get; private set; }
    public IAppPaths Paths { get; } = new FakePaths();
    public IPlatformCapabilities Capabilities { get; } = new FakeCapabilities();
    public Func<CancellationToken, Task>? InstallBehavior { get; set; }
    public Func<IProgress<DeviceCodeChallenge>, CancellationToken, Task<AccountProfile>>? MicrosoftBehavior { get; set; }
    public bool FailCredentialSave { get; set; }
    public string? LastSelectedAccountId { get; private set; }
    public string? LastCancelledDownloadId { get; private set; }
    public string? LastRetriedDownloadId { get; private set; }
    public string? LastLaunchServerId { get; private set; }
    public string? LastYggdrasilPassword { get; private set; }
    public Uri? LastYggdrasilServer { get; private set; }
    public IReadOnlyList<AuthenticationProfileChoice> UnboundYggdrasilProfiles { get; set; } = [];
    public bool ProfileSelectionOnWorker { get; set; }
    public int ProfileSelectorCalls { get; private set; }
    private int _outstandingProfileSelections;
    public int OutstandingProfileSelections => Volatile.Read(ref _outstandingProfileSelections);
    public string? LastSelectedProfileId { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int VanillaCreateCalls { get; private set; }
    public int FabricCreateCalls { get; private set; }
    public (string Name, string GameVersion, string LoaderVersion)? LastFabricRequest { get; private set; }
    public Func<CancellationToken, Task>? FabricCreateBehavior { get; set; }
    public bool FabricCreationCancelled { get; private set; }
    public Func<string, CancellationToken, Task<IReadOnlyList<JavaTrustFinding>>>? JavaTrustInspectBehavior { get; set; }
    public Func<string, CancellationToken, Task<JavaTrustDerivation>>? JavaTrustCopyBehavior { get; set; }
    public int JavaTrustInspectCalls { get; private set; }
    public int JavaTrustCopyCalls { get; private set; }
    public string? LastJavaTrustInspectPath { get; private set; }
    public string? LastJavaTrustCopyPath { get; private set; }
    public List<string> SavedSecretKinds { get; } = [];

    public FakeLauncherService()
    {
        Snapshot = new(new("潮涌核心社启动器", "1.5.0", "wyc", "b97b565678cbcdc8f602b57973d0837d3ac556a2", "Release", "SHOU.Launcher.Core", false, false),
            new(LauncherOS.MacOS, RuntimeArchitecture.Arm64, RuntimeArchitecture.Arm64, "test-fixture"), new(),
            [new("instance-1", "测试原版", "1.21.1", Path.Combine(Paths.InstancesDirectory, "fixture"), Installed: true)],
            [new("offline-1", AccountKind.Offline, "同名角色", "00000000000000000000000000000001")],
            [new("/fixture/java/bin/java", "/fixture/java", "21.0.13", 21, "Explicit test fake", RuntimeArchitecture.Arm64, "fixture")],
            [new("none", "只启动游戏", ""), new("smp", "社团生存", "smp.shoumc.com", "未知")], [], "offline-1", "instance-1");
    }
    public void Publish(LauncherSnapshot snapshot) { Snapshot = snapshot; _snapshotChanged?.Invoke(this, snapshot); }
    public void EmitLog(string message) => _logReceived?.Invoke(this, new(DateTimeOffset.UtcNow, "INFO", message));
    public Task InitializeAsync(CancellationToken cancellationToken = default) { Publish(Snapshot); return Task.CompletedTask; }
    public Task<GameInstance> CreateInstanceAsync(string name, string versionId, CancellationToken cancellationToken = default)
    {
        VanillaCreateCalls++;
        var instance = new GameInstance("instance-" + (Snapshot.Instances.Count + 1), name, versionId, Path.Combine(Paths.InstancesDirectory, "new-fixture"));
        Publish(Snapshot with { Instances = [.. Snapshot.Instances, instance], SelectedInstanceId = instance.Id });
        return Task.FromResult(instance);
    }
    public async Task<GameInstance> CreateFabricInstanceAsync(string name, string gameVersionId, string loaderVersion, CancellationToken cancellationToken = default)
    {
        FabricCreateCalls++;
        LastFabricRequest = (name, gameVersionId, loaderVersion);
        try { if (FabricCreateBehavior is not null) await FabricCreateBehavior(cancellationToken); }
        catch (OperationCanceledException) { FabricCreationCancelled = true; throw; }
        cancellationToken.ThrowIfCancellationRequested();
        var instance = new GameInstance("instance-" + (Snapshot.Instances.Count + 1), name, gameVersionId, Path.Combine(Paths.InstancesDirectory, "fabric-fixture"), Loader: "fabric", LoaderVersion: loaderVersion);
        Publish(Snapshot with { Instances = [.. Snapshot.Instances, instance], SelectedInstanceId = instance.Id });
        return instance;
    }
    public Task SelectInstanceAsync(string instanceId, CancellationToken cancellationToken = default) { Publish(Snapshot with { SelectedInstanceId = instanceId }); return Task.CompletedTask; }
    public Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot.JavaInstallations);
    public Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaTrustInspectCalls++; LastJavaTrustInspectPath = javaExecutable;
        return JavaTrustInspectBehavior?.Invoke(javaExecutable, cancellationToken) ?? Task.FromResult<IReadOnlyList<JavaTrustFinding>>([]);
    }
    public async Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string javaExecutable, CancellationToken cancellationToken = default)
    {
        JavaTrustCopyCalls++; LastJavaTrustCopyPath = javaExecutable;
        var result = await (JavaTrustCopyBehavior ?? throw new InvalidOperationException("Test must supply an explicit Java trust copy protocol fixture."))(javaExecutable, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Publish(Snapshot with { Settings = Snapshot.Settings with { JavaExecutable = result.Java.ExecutablePath },
            JavaInstallations = Snapshot.JavaInstallations.Where(j => j.ExecutablePath != result.Java.ExecutablePath).Append(result.Java).ToArray() });
        return result;
    }
    public async Task InstallInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        Publish(Snapshot with { State = "Installing", Error = null });
        try
        {
            if (InstallBehavior is not null) await InstallBehavior(cancellationToken);
            Publish(Snapshot with { State = "Ready", Instances = Snapshot.Instances.Select(i => i.Id == instanceId ? i with { Installed = true } : i).ToArray() });
        }
        catch (OperationCanceledException) { Publish(Snapshot with { State = "Ready" }); throw; }
        catch (Exception ex) { Publish(Snapshot with { State = "Error", Error = ex.Message }); throw; }
    }
    public Task<LaunchPlan> BuildLaunchPlanAsync(string instanceId, CancellationToken cancellationToken = default) => Task.FromResult(new LaunchPlan("/fixture/java/bin/java", RuntimeArchitecture.Arm64, "/fixture/game", ["--accessToken", "never-report-raw-plan"], new Dictionary<string, string?>(), [], [], [], ["fixture plan"]));
    public Task LaunchAsync(string instanceId, string serverId = "none", CancellationToken cancellationToken = default) { LastLaunchServerId = serverId; Publish(Snapshot with { State = "Running", GameProcessId = 1234 }); return Task.CompletedTask; }
    public Task StopGameAsync(CancellationToken cancellationToken = default) { StopCalls++; Publish(Snapshot with { State = "Ready", GameProcessId = null }); return Task.CompletedTask; }
    public Task CancelDownloadAsync(string taskId, CancellationToken cancellationToken = default) { LastCancelledDownloadId = taskId; Publish(Snapshot with { Downloads = Snapshot.Downloads.Select(t => t.Id == taskId ? t with { State = "Cancelled" } : t).ToArray() }); return Task.CompletedTask; }
    public Task RetryDownloadAsync(string taskId, CancellationToken cancellationToken = default) { LastRetriedDownloadId = taskId; Publish(Snapshot with { Downloads = Snapshot.Downloads.Select(t => t.Id == taskId ? t with { State = "Queued", Error = null } : t).ToArray() }); return Task.CompletedTask; }
    public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default) { SaveCalls++; Publish(Snapshot with { Settings = settings }); return Task.CompletedTask; }
    public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken cancellationToken = default)
    {
        if (FailCredentialSave) throw new CredentialStoreException(CredentialFailure.Locked, "安全存储已锁定，无法持久保存。");
        SavedSecretKinds.Add(kind);
        var settings = Snapshot.Settings;
        settings = kind == "aria2" ? settings with { Aria2 = (settings.Aria2 ?? new()) with { CredentialKey = "aria2-fixed-key" } }
            : settings with { Proxy = (settings.Proxy ?? new()) with { CredentialKey = "proxy-fixed-key" } };
        Publish(Snapshot with { Settings = settings });
        return Task.CompletedTask;
    }
    public Task<AccountProfile> AddOfflineAccountAsync(string playerName, CancellationToken cancellationToken = default)
    {
        var account = new AccountProfile("offline-" + (Snapshot.Accounts.Count + 1), AccountKind.Offline, playerName, Guid.NewGuid().ToString("N"));
        Publish(Snapshot with { Accounts = [.. Snapshot.Accounts, account], SelectedAccountId = account.Id });
        return Task.FromResult(account);
    }
    public async Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken cancellationToken = default)
    {
        Publish(Snapshot with { State = "Authenticating" });
        try
        {
            var profile = MicrosoftBehavior is not null ? await MicrosoftBehavior(challenge, cancellationToken)
                : new AccountProfile("ms-fixture", AccountKind.Microsoft, "测试微软账户", "00000000000000000000000000000002");
            Publish(Snapshot with { State = "Ready", Accounts = [.. Snapshot.Accounts, profile], SelectedAccountId = profile.Id });
            return profile;
        }
        catch { Publish(Snapshot with { State = "Ready" }); throw; }
    }
    public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string username, string password, CancellationToken cancellationToken = default)
    {
        LastYggdrasilPassword = password; LastYggdrasilServer = server;
        var profile = new AccountProfile("ygg-fixture", AccountKind.Yggdrasil, username, "00000000000000000000000000000003", server.AbsoluteUri);
        Publish(Snapshot with { Accounts = [.. Snapshot.Accounts, profile], SelectedAccountId = profile.Id });
        return Task.FromResult<IReadOnlyList<AccountProfile>>([profile]);
    }
    public async Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string username, string password,
        Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> selectProfile, CancellationToken cancellationToken = default)
    {
        LastYggdrasilPassword = password; LastYggdrasilServer = server;
        if (UnboundYggdrasilProfiles.Count == 0) return await LoginYggdrasilAsync(server, username, password, cancellationToken);
        Publish(Snapshot with { State = "Authenticating" });
        ProfileSelectorCalls++;
        Interlocked.Increment(ref _outstandingProfileSelections);
        try
        {
            var selectedId = ProfileSelectionOnWorker
                ? await Task.Run(() => selectProfile(UnboundYggdrasilProfiles, cancellationToken), cancellationToken).ConfigureAwait(false)
                : await selectProfile(UnboundYggdrasilProfiles, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (selectedId is null) throw new OperationCanceledException(cancellationToken);
            var selected = UnboundYggdrasilProfiles.Single(p => p.Id == selectedId);
            LastSelectedProfileId = selected.Id;
            var profile = new AccountProfile("ygg-fixture:" + selected.Id, AccountKind.Yggdrasil, selected.Name, selected.Id, server.AbsoluteUri);
            Publish(Snapshot with { Accounts = [.. Snapshot.Accounts, profile], SelectedAccountId = profile.Id, State = "Ready" });
            return [profile];
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingProfileSelections);
            if (Snapshot.State == "Authenticating") Publish(Snapshot with { State = "Ready" });
        }
    }
    public Task RefreshAccountAsync(string accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) { LastSelectedAccountId = accountId; Publish(Snapshot with { SelectedAccountId = accountId }); return Task.CompletedTask; }
    public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) { Publish(Snapshot with { Accounts = Snapshot.Accounts.Where(a => a.Id != accountId).ToArray(), SelectedAccountId = null }); return Task.CompletedTask; }
    public Task RefreshServersAsync(CancellationToken cancellationToken = default) { Publish(Snapshot with { Servers = Snapshot.Servers.Select(s => s.Id == "smp" ? s with { Status = "可达", OnlinePlayers = 7 } : s).ToArray() }); return Task.CompletedTask; }
    public IReadOnlyList<LauncherLog> GetRecentLogs() => [];
    public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
}

internal sealed class FakeExternalLauncher : IExternalLauncher
{
    public List<Uri> OpenedUris { get; } = [];
    public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) { OpenedUris.Add(uri); return Task.CompletedTask; }
    public Task OpenDirectoryAsync(string directory, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RevealFileAsync(string file, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
internal sealed class FakeCapabilities : IPlatformCapabilities
{
    public bool SupportsCredentialPersistence => true;
    public bool SupportsSystemProxy => true;
    public bool SupportsRevealFile => true;
    public IReadOnlyList<string> Limitations => [];
}
internal sealed class FakePaths : IAppPaths
{
    private readonly string Root = FixturePaths.Create("headless");
    public string DataDirectory => Root;
    public string SharedDataDirectory => Root + "/shared";
    public string LocalDataDirectory => Root + "/local";
    public string CacheDirectory => Root + "/cache";
    public string LogsDirectory => Root + "/logs";
    public string RuntimesDirectory => Root + "/runtimes";
    public string InstancesDirectory => Root + "/instances";
    public string TempDirectory => Root + "/temp";
    public string ResourcesDirectory => Root + "/resources";
    public string LockFile => Root + "/instance.lock";
    public string CredentialScope => "explicit-headless-test-fake";
    public bool IsIsolated => true;
}
