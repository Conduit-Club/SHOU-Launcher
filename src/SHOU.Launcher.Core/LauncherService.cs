using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Diagnostics;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Java;
using SHOU.Launcher.Core.Minecraft;
using ClubServer = SHOU.Launcher.Core.Abstractions.ClubServer;

namespace SHOU.Launcher.Core;

/// <summary>Production orchestration shared by both desktops. All persisted account DTOs are nonsecret.</summary>
public sealed partial class LauncherService : ILauncherService, ILocalPackService, ILegacyMicrosoftAccountMigrationService
{
    private readonly PlatformServices platform;
    private readonly HttpClient http;
    private readonly IVerifiedDownloader downloader;
    private readonly NetworkOptions network;
    private readonly BuildConfiguration build;
    private readonly ConfigurationStore config;
    private readonly AuthenticationClient authentication;
    private readonly JavaBootstrapArtifact? javaBootstrap;
    private readonly IJavaTrustService javaTrust;
    private readonly SemaphoreSlim initialize = new(1, 1), operation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly ConcurrentDictionary<string, AuthSession> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadTaskSnapshot> downloads = new(StringComparer.Ordinal);
    private readonly Queue<LauncherLog> logs = new();
    private readonly LogRedactor redactor = new();
    private LauncherSnapshot snapshot;
    private bool initialized, disposed;
    private CancellationTokenSource? installCancellation;
    private readonly ConcurrentDictionary<string, (IManagedProcess Process, bool IndependentLog)> games = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> gameObservers = new(StringComparer.Ordinal);
    private readonly GameSessionRegistry gameSessions;
    private DateTimeOffset lastDownloadUpdate;
    private readonly DownloadTaskRegistry downloadOwners = new();
    private readonly string launcherLogPath;

    public LauncherService(PlatformServices platform, HttpClient http, IVerifiedDownloader downloader,
        NetworkOptions network, BuildConfiguration? buildConfiguration = null, AuthenticationClient? authenticationClient = null,
        JavaBootstrapArtifact? javaBootstrap = null, IJavaTrustService? javaTrust = null, Content.CurseForgePackResolver? localPackResolver = null)
    {
        this.platform = platform; this.http = http; this.downloader = downloader; this.network = network;
        build = buildConfiguration ?? new(); config = new(platform.Paths);
        authentication = authenticationClient ?? new(http);
        this.localPackResolver = localPackResolver;
        this.javaBootstrap = javaBootstrap;
        this.javaTrust = javaTrust ?? new DerivedJavaTrustService(platform.JavaDiscovery, platform.Processes);
        network.Credentials = platform.Credentials;
        launcherLogPath = Path.Combine(platform.Paths.LogsDirectory, "launcher.log");
        gameSessions = new(Path.Combine(platform.Paths.LocalDataDirectory, "game-sessions"));
        snapshot = new(build.GetIdentity(), platform.Info.Context, new(), [], [], [],
            [new("none", "只启动游戏", ""), new("smp", "SMP · 多人生存", "smp.shoumc.com"),
             new("create", "Create · 创造建筑", "create.shoumc.com"), new("shou", "SHOU · 校园展示", "shou.shoumc.com")], []);
    }
    public LauncherSnapshot Snapshot { get { lock (sync) return snapshot; } }
    public IAppPaths Paths => platform.Paths;
    public IPlatformCapabilities Capabilities => platform.Capabilities;
    public event EventHandler<LauncherSnapshot>? SnapshotChanged;
    public event EventHandler<LauncherLog>? LogReceived;
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initialize.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return;
            if (!GeneratedLifecycle.Entries.SequenceEqual(new[] { "Paths", "DataLock", "Configuration", "Credentials", "Network", "Accounts", "UI" }))
                throw new InvalidOperationException("Generated startup contract is not compatible with Bootstrap.");
            foreach (var path in new[] { Paths.DataDirectory, Paths.SharedDataDirectory, Paths.LocalDataDirectory, Paths.CacheDirectory,
                         Paths.LogsDirectory, Paths.RuntimesDirectory, Paths.InstancesDirectory, Paths.TempDirectory }) Directory.CreateDirectory(path);
            var state = await config.LoadAsync(cancellationToken).ConfigureAwait(false);
            ConfigurationStore.ValidateSettings(state.Settings);
            if (state.Settings.GameRoot is { Length: > 0 } gameRoot) EnsureIsolatedGamePath(gameRoot);
            foreach (var instance in state.Instances) EnsureIsolatedGamePath(instance.GameDirectory);
            network.Update(state.Settings);
            Set(s => s with { Settings = state.Settings, Instances = state.Instances, Accounts = state.Accounts,
                SelectedAccountId = state.SelectedAccountId, SelectedInstanceId = state.SelectedInstanceId });
            foreach (var account in state.Accounts.Where(a => a.Kind != AccountKind.Offline))
            {
                try
                {
                    var bytes = await platform.Credentials.ReadAsync(AccountKey(account.Id), cancellationToken).ConfigureAwait(false);
                    if (bytes is not null)
                    {
                        try
                        {
                            var session = JsonSerializer.Deserialize<AuthSession>(bytes, ConfigurationStore.JsonOptions) ?? throw new InvalidDataException("Empty credential record.");
                            if (!AccountIdentity.Matches(account, session.Profile)) throw new InvalidDataException("Protected account identity/origin mismatch.");
                            var trusted = session.Profile with { Persisted = true, Status = "已保存 · 启动时验证" };
                            RegisterSession(session); sessions[account.Id] = session with { Profile = trusted }; ReplaceAccount(trusted);
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    else ReplaceAccount(account with { Persisted = false, Status = "凭据缺失 · 请重新登录" });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is CredentialStoreException or JsonException or InvalidDataException or IOException)
                {
                    Set(s => s with { CanPersistAccounts = false });
                    ReplaceAccount(account with { Status = "凭据读取失败 · 原数据保留" });
                    Log("Warning", "账户凭据无法读取，已禁止账户持久写入；原因类型：" + ex.GetType().Name);
                }
            }
            initialized = true;
            RefreshGameSessions();
            sessionMonitor = MonitorGameSessionsAsync();
            Log("Info", $"{Snapshot.Build.Name} {Snapshot.Build.Version} · {Snapshot.Platform.RuntimeIdentifier} · configuration owner={build.OwnerAssembly}");
        }
        finally { initialize.Release(); }
    }
    public async Task<GameInstance> CreateInstanceAsync(string name, string versionId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("请输入1–80字的单行实例名称。");
        SafePath.Identifier(versionId);
        var instance = new GameInstance(Guid.NewGuid().ToString("N"), name.Trim(), versionId,
            SafePath.Within(Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory, Guid.NewGuid().ToString("N")), GameVersionId: versionId);
        await ExecuteAsync("Ready", async ct =>
        {
            await RegisterNewInstanceAsync(instance, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return instance;
    }
    public Task SelectInstanceAsync(string instanceId, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    { _ = FindInstance(instanceId); await PersistCandidateAsync(Snapshot with { SelectedInstanceId = instanceId }, ct).ConfigureAwait(false); Set(s => s with { SelectedInstanceId = instanceId }); }, cancellationToken);
    public async Task<GameInstance> CreateFabricInstanceAsync(string name, string gameVersionId, string loaderVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("请输入1–80字的单行实例名称。");
        _ = FabricMetadataProvider.ProfileId(gameVersionId, loaderVersion);
        GameInstance? created = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var profile = await new FabricMetadataProvider(http, Paths.CacheDirectory, Sources).CacheProfileAsync(gameVersionId, loaderVersion, ct).ConfigureAwait(false);
            created = new(Guid.NewGuid().ToString("N"), name.Trim(), profile,
                SafePath.Within(Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory, Guid.NewGuid().ToString("N")), Loader: "fabric", LoaderVersion: loaderVersion, GameVersionId: gameVersionId);
            await RegisterNewInstanceAsync(created, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return created!;
    }
    public async Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = await platform.JavaDiscovery.DiscoverAsync(new(Snapshot.Settings.JavaExecutable, Paths.RuntimesDirectory), cancellationToken).ConfigureAwait(false);
        Set(s => s with { JavaInstallations = result }); return result;
    }
    public Task InstallInstanceAsync(string instanceId, CancellationToken cancellationToken = default) => ExecuteAsync("Installing", async ct =>
    { _ = await PrepareInstanceAsync(FindInstance(instanceId), ct).ConfigureAwait(false); }, cancellationToken);
    private async Task<(InstalledGame Game, JavaInstallation Java)> PrepareInstanceAsync(GameInstance instance, CancellationToken ct)
    {
        EnsureInstanceNotRunning(instance);
        SetLaunchStage("检查游戏文件");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ownership = downloadOwners.Begin(instance.Id); installCancellation = cancellation;
        try { return await PrepareInstanceCoreAsync(instance, cancellation.Token).ConfigureAwait(false); }
        finally { downloadOwners.End(ownership); installCancellation = null; }
    }
    private async Task<(InstalledGame Game, JavaInstallation Java)> PrepareInstanceCoreAsync(GameInstance instance, CancellationToken ct)
    {
        EnsureIsolatedGamePath(instance.GameDirectory);
        if (instance.Loader is not ("vanilla" or "fabric" or "quilt" or "forge" or "neoforge")) throw new PlatformNotSupportedException("当前预览尚未实现此loader的processor链。");
        if (instance.Loader != "vanilla") _ = Instances.InstanceIdentity.GameVersion(instance);
        var resolver = new VersionMetadataResolver(http, downloader, Paths.CacheDirectory, Sources);
        if (instance.Loader is "forge" or "neoforge")
        {
            var vanilla = await resolver.ResolveAsync(Instances.InstanceIdentity.GameVersion(instance), ct).ConfigureAwait(false);
            var processorJava = await SelectInstanceJavaAsync(instance, vanilla, ct).ConfigureAwait(false);
            await OfficialLoaders.EnsureInstalledAsync(instance, vanilla, processorJava, Context(processorJava), new InlineProgress<TransferProgress>(OnTransfer), ct).ConfigureAwait(false);
        }
        var version = await resolver.ResolveAsync(instance.VersionId, ct).ConfigureAwait(false);
        ValidateLoaderProfile(instance, version);
        var java = await SelectInstanceJavaAsync(instance, version, ct).ConfigureAwait(false);
        var context = Context(java);
        var gameFiles = await new MinecraftInstaller(http, downloader, Paths.CacheDirectory, Sources).InstallAsync(version, context,
            instance.GameDirectory, new InlineProgress<TransferProgress>(OnTransfer), ct).ConfigureAwait(false);
        var installedInstances = Snapshot.Instances.Select(i => i.Id == instance.Id ? i with { Installed = true } : i).ToArray();
        await PersistCandidateAsync(Snapshot with { Instances = installedInstances }, ct).ConfigureAwait(false);
        Set(s => s with { Instances = installedInstances });
        return (gameFiles, java);
    }
    private async Task<JavaInstallation> SelectInstanceJavaAsync(GameInstance instance, ResolvedVersion version, CancellationToken ct)
    {
        SetLaunchStage("选择 Java");
        var preferred = instance.JavaExecutable ?? Snapshot.Settings.JavaExecutable;
        var candidates = await platform.JavaDiscovery.DiscoverAsync(new(preferred, Paths.RuntimesDirectory, version.RequiredJavaMajor,
            Snapshot.Platform.HardwareArchitecture), ct).ConfigureAwait(false);
        Set(s => s with { JavaInstallations = candidates });
        var java = candidates.Where(j => j.IsUsable && j.MajorVersion == version.RequiredJavaMajor
            && j.RuntimeArchitecture == Snapshot.Platform.HardwareArchitecture && (string.IsNullOrWhiteSpace(preferred) || PathEquals(j.ExecutablePath, preferred)))
            .OrderByDescending(j => string.IsNullOrWhiteSpace(preferred) && IsDerivedJava(j)).FirstOrDefault();
        var installedRuntime = java is null;
        if (java is null)
        {
            if (!string.IsNullOrWhiteSpace(preferred)) throw new InvalidOperationException("指定Java未通过版本/架构探测；请修正选择，不会静默替换用户指定runtime。");
            java = await platform.JavaInstaller.InstallAsync(new(version.JavaComponent, version.RequiredJavaMajor, Snapshot.Platform.OS,
                Snapshot.Platform.HardwareArchitecture, Paths.RuntimesDirectory), new InlineProgress<TransferProgress>(OnTransfer), ct).ConfigureAwait(false);
            Set(s => s with { JavaInstallations = s.JavaInstallations.Append(java).ToArray() });
        }
        if (IsDerivedJava(java) || installedRuntime && Snapshot.Settings.ConduitJavaTrustAutoRepair
            && java.Source.StartsWith("managed:Mojang", StringComparison.Ordinal))
        {
            if (installedRuntime && java.ValidatedManifestSha1 is null)
                throw new InvalidDataException("官方下载Java缺少已验证manifest身份，未进行自动派生修复。");
            var derivation = await DeriveJavaAsync(java, ct).ConfigureAwait(false);
            java = derivation.Java;
            Set(s => s with { JavaInstallations = s.JavaInstallations.Where(j => !SameJavaPath(j.ExecutablePath, java.ExecutablePath)).Append(java).ToArray() });
        }
        return java;
    }
    public async Task<LaunchPlan> BuildLaunchPlanAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        LaunchPlan? plan = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var instance = FindInstance(instanceId); var prepared = await PrepareInstanceAsync(instance, ct).ConfigureAwait(false);
            var original = await PlanAsync(instance, prepared.Game, prepared.Java, ct).ConfigureAwait(false);
            using var protection = ProtectPlan(original);
            plan = protection?.Plan ?? original;
        }, cancellationToken).ConfigureAwait(false);
        return plan!;
    }
    private async Task<LaunchPlan> PlanAsync(GameInstance instance, InstalledGame installed, JavaInstallation java, CancellationToken ct)
    {
        SetLaunchStage("验证账户");
        var account = Snapshot.Accounts.FirstOrDefault(a => a.Id == Snapshot.SelectedAccountId) ?? throw new InvalidOperationException("请先选择账户或添加本地离线测试档案。");
        if (account.Kind != AccountKind.Offline && javaBootstrap is null)
            throw new InvalidOperationException("此构建缺少受信Java引导，已拒绝将真实账户凭据放入命令行。请使用完整候选包。");
        LaunchIdentity identity;
        if (account.Kind == AccountKind.Offline) identity = new(account.DisplayName, account.PlayerUuid, "0");
        else
        {
            if (!sessions.TryGetValue(account.Id, out var session)) throw new AuthenticationException("missing_credentials", "账户凭据不可用，请重新登录。");
            if (account.ExpiresAt is null || account.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(2)) session = await RefreshInternalAsync(account, ct).ConfigureAwait(false);
            identity = new(account.DisplayName, account.PlayerUuid, session.AccessToken, account.Kind == AccountKind.Microsoft ? "msa" : "legacy", session.Xuid,
                account.Kind == AccountKind.Microsoft ? ResolveClientId() : session.ClientToken);
            if (account.Kind == AccountKind.Yggdrasil)
            {
                var server = new Uri(AuthenticationClient.NormalizeYggdrasilRoot(new Uri(account.AuthenticationServer!)));
                var descriptor = JsonNode.Parse(await http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", ct).ConfigureAwait(false))?.AsObject() ?? throw new InvalidDataException("Authlib descriptor invalid.");
                var url = (string?)descriptor["download_url"] ?? throw new InvalidDataException("Authlib download URL missing.");
                var sha256 = (string?)descriptor["checksums"]?["sha256"] ?? throw new InvalidDataException("Authlib checksum missing.");
                var injector = SafePath.Within(Paths.CacheDirectory, "authlib/" + sha256 + ".jar");
                await downloader.DownloadAsync(new([new Uri(url)], injector, Sha256: sha256), new InlineProgress<TransferProgress>(OnTransfer), ct).ConfigureAwait(false);
                var metadata = await http.GetStringAsync(server, ct).ConfigureAwait(false);
                _ = JsonNode.Parse(metadata) as JsonObject ?? throw new InvalidDataException("Auth server metadata invalid.");
                identity = identity with { AuthenticationServer = server, InjectorPath = injector, PrefetchedMetadataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata)) };
            }
        }
        var temporary = SafePath.Within(Paths.TempDirectory, "game-" + SafePath.Identifier(instance.Id));
        Directory.CreateDirectory(temporary);
        return LaunchPlanBuilder.Build(new(installed.Version, java, Context(java), identity, instance.GameDirectory,
            installed.LibrariesDirectory, installed.AssetsDirectory, installed.NativesDirectory, installed.ClientJar,
            installed.Assets.Id, instance.MemoryMiB ?? Snapshot.Settings.MemoryMiB, installed.LegacyExtractions, TemporaryDirectory: temporary,
            WindowWidth: Snapshot.Settings.GameWindowWidth, WindowHeight: Snapshot.Settings.GameWindowHeight,
            Fullscreen: Snapshot.Settings.GameFullscreen, MinimumMemoryMiB: Snapshot.Settings.MinimumMemoryMiB, CustomInfo: Snapshot.Settings.CustomInfo));
    }
    private ProtectedJavaLaunch? ProtectPlan(LaunchPlan original)
    {
        if (javaBootstrap is null) return null; // Offline-only fixtures remain runnable without a packaged helper.
        var token = Snapshot.SelectedAccountId is { } id && sessions.TryGetValue(id, out var session) ? session.AccessToken : null;
        return ProtectedJavaLaunch.Prepare(original, javaBootstrap, Snapshot.Platform.MinecraftOS == "windows" ? ';' : ':', token);
    }
    public Task LaunchAsync(string instanceId, string serverId = "none", CancellationToken cancellationToken = default) => ExecuteAsync("Launching", async ct =>
    {
        if (serverId != "none") throw new PlatformNotSupportedException("自动入服尚待连接验收，请先选择“只启动游戏”，进入游戏后使用多人游戏连接服务器。");
        var instance = FindInstance(instanceId);
        EnsureInstanceNotRunning(instance);
        var account = Snapshot.Accounts.FirstOrDefault(a => a.Id == Snapshot.SelectedAccountId);
        Set(s => s with { LaunchActivity = new(instance.Id, instance.Name, "准备游戏", account?.Kind == AccountKind.Microsoft ? "微软正版验证" : account?.Kind == AccountKind.Yggdrasil ? "外置账户验证" : "离线角色") });
        var prepared = await PrepareInstanceAsync(instance, ct).ConfigureAwait(false);
        var original = await PlanAsync(instance, prepared.Game, prepared.Java, ct).ConfigureAwait(false);
        // The verified Java bootstrap owns output after handoff, including when the launcher exits.
        if (javaBootstrap is not null)
        {
            VerifiedDownloader.RejectLink(Paths.LogsDirectory);
            var log = Path.Combine(Paths.LogsDirectory, "game-" + Guid.NewGuid().ToString("N") + ".log");
            var arguments = original.Arguments.ToList();
            arguments.Insert(original.MainClassIndex, "-Dshou.launcher.sessionLog=" + log);
            original = original with { Arguments = arguments, MainClassIndex = original.MainClassIndex + 1 };
        }
        using var protection = ProtectPlan(original);
        var plan = protection?.Plan ?? original;
        var reportPath = SafePath.Within(Paths.LogsDirectory, "launch-plan-redacted.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(plan.ToRedactedReport(), ConfigurationStore.JsonOptions), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        SetLaunchStage("启动游戏进程");
        var collector = new ProcessLogCollector((error, line) => Log(error ? "GameError" : "Game", line));
        var process = await platform.Processes.StartAsync(new(plan.JavaExecutable, plan.Arguments, plan.WorkingDirectory, plan.EnvironmentOverrides,
            OutputLimitCharacters: 4 * 1024 * 1024, SensitiveInput: protection?.Input,
            OnStarted: protection is null ? null : pid => gameSessions.Register(instance, pid)), new InlineProgress<ProcessOutput>(collector.Report), CancellationToken.None).ConfigureAwait(false);
        var independent = protection is not null && process is IDetachableProcess;
        try
        {
            ct.ThrowIfCancellationRequested();
            games[instanceId] = (process, independent);
            RefreshGameSessions();
            var observer = ObserveGameAsync(instanceId, process, collector);
            gameObservers[instanceId] = observer;
            SetLaunchStage("等待游戏窗口");
            if (!process.Completion.IsCompleted && platform.Processes is IGameWindowObserver windowObserver)
            {
                var visible = await windowObserver.WaitForGameWindowAsync(process, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
                if (!visible && !process.Completion.IsCompleted) Log("Warning", "游戏进程已启动，暂未确认窗口；可继续操作启动器，游戏日志保存在本机。");
            }
            ct.ThrowIfCancellationRequested();
            if (process.Completion.IsCompleted) await observer.ConfigureAwait(false);
        }
        catch { await process.StopAsync().ConfigureAwait(false); await process.DisposeAsync().ConfigureAwait(false); games.TryRemove(instanceId, out _); RefreshGameSessions(); throw; }
        Log("Info", "已启动本次Java游戏进程，PID=" + process.ProcessId + "；主菜单仍需独立真实观察。");
        await RecordInstanceLaunchAsync(instanceId).ConfigureAwait(false);
    }, cancellationToken);
    private async Task ObserveGameAsync(string instanceId, IManagedProcess process, ProcessLogCollector collector)
    {
        try
        {
            var result = await process.Completion.ConfigureAwait(false);
            if (result.Detached) return;
            collector.Flush(result.OutputTruncated);
            Log(result.ExitCode == 0 ? "Info" : "Error", $"本次游戏进程退出：code={result.ExitCode}, cancelled={result.Cancelled}, timeout={result.TimedOut}。");
            if (result.ExitCode != 0 && !result.Cancelled && !disposed)
                Set(s => s with { State = s.State == "Ready" ? "Error" : s.State, Error = "游戏异常退出，请查看日志。" });
        }
        catch (Exception ex) { if (!disposed) { Log("Error", "游戏进程观察失败：" + ex.GetType().Name); Set(s => s with { Error = "无法完整读取游戏退出状态。" }); } }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
            if (games.TryGetValue(instanceId, out var current) && ReferenceEquals(current.Process, process)) games.TryRemove(instanceId, out _);
            if (!disposed) RefreshGameSessions();
        }
    }
    public async Task StopGameAsync(CancellationToken cancellationToken = default)
    { if (Snapshot.SelectedInstanceId is { } id && games.TryGetValue(id, out var current)) await current.Process.StopAsync(cancellationToken).ConfigureAwait(false); }
    public Task CancelDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = installCancellation ?? throw new InvalidOperationException("当前没有可取消的安装下载。");
        downloadOwners.RequireActive(taskId);
        current.Cancel(); return Task.CompletedTask;
    }
    public async Task RetryDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var owner = downloadOwners.Resolve(taskId);
        if (owner.InstanceId.StartsWith("minecraft-package:", StringComparison.Ordinal))
        {
            if (!packageTransfers.TryGetValue(owner.Generation, out var package)) throw new InvalidOperationException("该文件记录已失效，请重新选择版本。");
            await DownloadMinecraftPackageAsync(package.Version, package.Kind, package.Destination, cancellationToken).ConfigureAwait(false); return;
        }
        if (await TryRetryContentOperationAsync(owner.Generation, cancellationToken).ConfigureAwait(false)) return;
        if (owner.InstanceId.StartsWith("content:", StringComparison.Ordinal))
        {
            if (!contentTransfers.TryGetValue(owner.Generation, out var file)) throw new InvalidOperationException("该资源下载记录已失效，请重新选择文件。");
            _ = await DownloadContentAsync(file, cancellationToken).ConfigureAwait(false);
        }
        else await InstallInstanceAsync(owner.InstanceId, cancellationToken).ConfigureAwait(false);
    }
    public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        ConfigurationStore.ValidateSettings(settings);
        if (settings.MinimumMemoryMiB is { } initial && Snapshot.Instances.FirstOrDefault(i => (i.MemoryMiB ?? settings.MemoryMiB) < initial) is { } conflict)
            throw new ArgumentException($"初始内存不能高于实例“{conflict.Name}”的最大内存，请先修改该实例或降低初始内存。");
        if (settings.GameRoot is { Length: > 0 } gameRoot) EnsureIsolatedGamePath(gameRoot);
        await PersistCandidateAsync(Snapshot with { Settings = settings }, ct).ConfigureAwait(false);
        network.Update(settings); Set(s => s with { Settings = settings });
    }, cancellationToken);
    public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        if (kind is not ("aria2" or "proxy")) throw new ArgumentException("Unknown credential kind.");
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("凭据不能为空。");
        redactor.Register(secret);
        var key = "connection:" + kind;
        await platform.Credentials.WriteAsync(key, Encoding.UTF8.GetBytes(secret), ct).ConfigureAwait(false);
        var settings = Snapshot.Settings;
        settings = kind == "aria2" ? settings with { Aria2 = (settings.Aria2 ?? new()) with { CredentialKey = key } }
            : settings with { Proxy = (settings.Proxy ?? new()) with { CredentialKey = key } };
        await PersistCandidateAsync(Snapshot with { Settings = settings }, ct).ConfigureAwait(false);
        network.Update(settings); Set(s => s with { Settings = settings });
    }, cancellationToken);
    public async Task<AccountProfile> AddOfflineAccountAsync(string playerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerName) || playerName.Length is < 3 or > 16 || playerName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("离线档案名须为3–16位英文字母、数字或下划线。");
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName)); bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var uuid = Convert.ToHexString(bytes).ToLowerInvariant();
        AccountProfile? result = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var account = new AccountProfile("offline:" + uuid, AccountKind.Offline, playerName, uuid,
                Persisted: Snapshot.CanPersistAccounts,
                Status: Snapshot.CanPersistAccounts ? "本地离线档案 · 无法进入需认证社团服" : "当前会话离线档案 · 持久保存已禁用");
            var next = AccountCandidate(Snapshot, account);
            await PersistCandidateAsync(next, ct).ConfigureAwait(false);
            Set(s => s with { Accounts = next.Accounts, SelectedAccountId = account.Id }); result = account;
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken cancellationToken = default)
    {
        AccountProfile? result = null;
        await ExecuteAsync("Authenticating", async ct => { var session = await authentication.LoginMicrosoftAsync(ResolveClientId(), challenge, ct).ConfigureAwait(false); result = await SaveSessionAsync(session, ct).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
    public async Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string username, string password, CancellationToken cancellationToken = default)
        => await LoginYggdrasilCoreAsync(server, username, password, null, cancellationToken).ConfigureAwait(false);
    public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string username, string password,
        Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> selectProfile, CancellationToken cancellationToken = default)
        => LoginYggdrasilCoreAsync(server, username, password, selectProfile, cancellationToken);
    private async Task<IReadOnlyList<AccountProfile>> LoginYggdrasilCoreAsync(Uri server, string username, string password,
        Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>>? selectProfile, CancellationToken cancellationToken)
    {
        redactor.Register(password); redactor.Register(username);
        var result = new List<AccountProfile>();
        await ExecuteAsync("Authenticating", async ct =>
        { foreach (var session in await authentication.LoginYggdrasilAsync(server, username, password, ct, selectProfile).ConfigureAwait(false)) result.Add(await SaveSessionAsync(session, ct).ConfigureAwait(false)); }, cancellationToken).ConfigureAwait(false);
        return result;
    }
    private async Task<AccountProfile> SaveSessionAsync(AuthSession session, CancellationToken ct)
    {
        RegisterSession(session); var account = session.Profile;
        if (Snapshot.CanPersistAccounts)
        {
            try
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(session, ConfigurationStore.JsonOptions);
                try { await platform.Credentials.WriteAsync(AccountKey(account.Id), data, ct).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(data); }
                account = account with { Persisted = true, Status = "已保存 · 启动时验证" };
            }
            catch (CredentialStoreException ex)
            { Set(s => s with { CanPersistAccounts = false }); account = account with { Persisted = false, Status = "当前会话可用 · 凭据无法持久保存" }; Log("Warning", "凭据持久保存失败；原数据保留，未降明文。状态=" + ex.Failure); }
        }
        else account = account with { Persisted = false, Status = "当前会话可用 · 持久保存已禁用" };
        // Publish a saved account only after both the protected credential write and sidecar commit.
        // A failed sidecar must not leave the UI claiming that a newly authorized account was saved.
        var next = AccountCandidate(Snapshot, account);
        try { await PersistCandidateAsync(next, ct).ConfigureAwait(false); }
        catch
        {
            // Refresh may already have rotated the protected token. Keep that valid in-memory
            // session, but freeze account persistence and never append an unsaved new account.
            var temporary = account with { Persisted = false, Status = "当前会话可用 · 档案保存失败，原列表保留" };
            sessions[account.Id] = session with { Profile = temporary };
            Set(s => s with { CanPersistAccounts = false,
                Accounts = s.Accounts.Select(a => a.Id == account.Id ? temporary : a).ToArray() });
            throw;
        }
        sessions[account.Id] = session with { Profile = account };
        Set(s => s with { Accounts = next.Accounts, SelectedAccountId = account.Id }); return account;
    }
    public Task RefreshAccountAsync(string accountId, CancellationToken cancellationToken = default) => ExecuteAsync("Authenticating", async ct =>
    { _ = await RefreshInternalAsync(FindAccount(accountId), ct).ConfigureAwait(false); }, cancellationToken);
    private async Task<AuthSession> RefreshInternalAsync(AccountProfile account, CancellationToken ct)
    {
        if (account.Kind == AccountKind.Offline) throw new InvalidOperationException("离线档案无需刷新。");
        if (!sessions.TryGetValue(account.Id, out var current)) throw new AuthenticationException("missing_credentials", "请重新登录此账户。");
        try
        {
            var refreshed = account.Kind == AccountKind.Microsoft ? await authentication.RefreshMicrosoftAsync(ResolveClientId(), current, ct).ConfigureAwait(false)
                : await authentication.RefreshYggdrasilAsync(current, ct).ConfigureAwait(false);
            _ = await SaveSessionAsync(refreshed, ct).ConfigureAwait(false); return sessions[account.Id];
        }
        catch (AuthenticationException) { ReplaceAccount(account with { Status = "凭据已失效 · 请重新登录" }); throw; }
    }
    public Task SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        _ = FindAccount(accountId);
        await PersistCandidateAsync(Snapshot with { SelectedAccountId = accountId }, ct).ConfigureAwait(false);
        Set(s => s with { SelectedAccountId = accountId });
    }, cancellationToken);
    public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) => ExecuteAsync("Ready", async ct =>
    {
        var account = FindAccount(accountId);
        if (!Snapshot.CanPersistAccounts && account.Kind != AccountKind.Offline) throw new InvalidOperationException("凭据读取曾失败，删除写入已禁用以保护原数据。");
        if (account.Kind != AccountKind.Offline) await platform.Credentials.DeleteAsync(AccountKey(accountId), ct).ConfigureAwait(false);
        sessions.TryRemove(accountId, out _); Set(s => s with { Accounts = s.Accounts.Where(a => a.Id != accountId).ToArray(), SelectedAccountId = s.SelectedAccountId == accountId ? null : s.SelectedAccountId });
        await PersistAsync(ct).ConfigureAwait(false);
    }, cancellationToken);
    public async Task RefreshServersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var current = Snapshot.Servers;
        var refreshed = await Task.WhenAll(current.Select(async server => server.Id == "none" ? server : await ClubStatusProbe.ProbeAsync(server, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        Set(s => s with { Servers = refreshed });
    }
    public IReadOnlyList<LauncherLog> GetRecentLogs() { lock (sync) return logs.ToArray(); }
    private async Task ExecuteAsync(string state, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        if (!await operation.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("另一项操作尚在运行，请等待或取消。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            Set(s => s with { State = state, Error = null });
            await action(linked.Token).ConfigureAwait(false);
            // A process observer can already have reported failure before StartAsync returns.
            // Only complete our own transitional state; never overwrite a newer Error/Running.
            Set(s => s.State == state ? s with { State = s.Error is null ? "Ready" : "Error" } : s);
        }
        catch (OperationCanceledException) { MarkDownloads("Cancelled", null); Set(s => s with { State = "Ready", Error = null }); throw; }
        catch (Exception ex) { var message = SafeError(ex); MarkDownloads("Failed", message); Set(s => s with { State = "Error", Error = message }); Log("Error", message); throw; }
        finally { if (state == "Launching") Set(s => s with { LaunchActivity = null }); operation.Release(); }
    }
    private Task PersistAsync(CancellationToken ct)
    {
        return PersistCandidateAsync(Snapshot, ct);
    }
    private Task PersistCandidateAsync(LauncherSnapshot state, CancellationToken ct)
    {
        // If reading any credential failed, do not overwrite the persisted account list with a partial view.
        var savedAccounts = state.Accounts.Where(a => a.Kind == AccountKind.Offline || a.Persisted).ToArray();
        var savedSelection = savedAccounts.Any(a => a.Id == state.SelectedAccountId) ? state.SelectedAccountId : null;
        return config.SaveAsync(new(state.Settings, state.Instances, savedAccounts, savedSelection, state.SelectedInstanceId), ct, writeAccounts: state.CanPersistAccounts);
    }
    private static LauncherSnapshot AccountCandidate(LauncherSnapshot state, AccountProfile account) => state with
    { Accounts = state.Accounts.Where(a => a.Id != account.Id).Append(account).ToArray(), SelectedAccountId = account.Id };
    private void OnTransfer(TransferProgress progress)
    {
        SetLaunchTransfer(progress);
        var id = downloadOwners.Register(progress.Id);
        downloads[id] = new(id, Path.GetFileName(progress.Id), progress.Stage, progress.BytesReceived, progress.TotalBytes, progress.Source);
        lock (sync)
        {
            if (DateTimeOffset.UtcNow - lastDownloadUpdate < TimeSpan.FromMilliseconds(120) && progress.Stage != "Completed") return;
            lastDownloadUpdate = DateTimeOffset.UtcNow;
        }
        Set(s => s with { Downloads = downloads.Values.TakeLast(250).ToArray() });
    }
    private void MarkDownloads(string state, string? error)
    { foreach (var item in downloads.ToArray()) if (item.Value.State is "Queued" or "Downloading") downloads[item.Key] = item.Value with { State = state, Error = error }; Set(s => s with { Downloads = downloads.Values.TakeLast(250).ToArray() }); }
    private void RegisterSession(AuthSession session)
    { redactor.Register(session.AccessToken); redactor.Register(session.RefreshToken); redactor.Register(session.ClientToken); redactor.Register(session.Profile.PlayerUuid); redactor.Register(session.Profile.DisplayName); }
    private void ReplaceAccount(AccountProfile account) => Set(s => s with { Accounts = s.Accounts.Where(a => a.Id != account.Id).Append(account).ToArray() });
    private GameInstance FindInstance(string id) => Snapshot.Instances.FirstOrDefault(i => i.Id == id) ?? throw new KeyNotFoundException("找不到实例。");
    private AccountProfile FindAccount(string id) => Snapshot.Accounts.FirstOrDefault(a => a.Id == id) ?? throw new KeyNotFoundException("找不到账户。");
    private static string AccountKey(string id) => "account:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    private string ResolveClientId() => AuthenticationClient.ResolveMicrosoftClientId(null, Snapshot.Settings.MicrosoftClientId, build);
    private IReadOnlyList<DownloadSource> Sources() => DownloadSources.Parse(Snapshot.Settings.DownloadSourcesJson);
    private RuleContext Context(JavaInstallation java) => new(Snapshot.Platform.MinecraftOS, Snapshot.Platform.OSVersion, java.RuntimeArchitecture,
        new Dictionary<string, bool> { ["is_demo_user"] = false, ["has_custom_resolution"] = Snapshot.Settings.GameWindowWidth is not null, ["has_quick_plays_support"] = false });
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private void EnsureIsolatedGamePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("实例游戏路径必须为绝对路径。");
        if (!Paths.IsIsolated) return;
        if (!SafePath.IsWithin(SafePath.Canonical(Paths.DataDirectory), SafePath.Canonical(path)))
            throw new InvalidDataException("隔离模式的游戏目录不能越出数据根；请先显式重新映射旧路径。");
    }
    private string SafeError(Exception ex) => ex switch
    { AuthenticationException auth => redactor.Scrub(auth.Message), CredentialStoreException cred => "凭据服务不可用（" + cred.Failure + "），原数据保留。", AggregateException => "下载或元数据所有来源均失败，请检查网络并重试。", _ => redactor.Scrub(ex.Message) };
    private void Set(Func<LauncherSnapshot, LauncherSnapshot> change)
    {
        LauncherSnapshot next; lock (sync) snapshot = next = change(snapshot);
        if (SnapshotChanged is { } changed) foreach (EventHandler<LauncherSnapshot> subscriber in changed.GetInvocationList()) { try { subscriber(this, next); } catch { /* UI observers cannot corrupt orchestration. */ } }
    }
    private void Log(string level, string message)
    {
        var log = new LauncherLog(DateTimeOffset.UtcNow, level, redactor.Scrub(message));
        lock (sync)
        {
            logs.Enqueue(log); while (logs.Count > 500) logs.Dequeue();
            try
            {
                if (Directory.Exists(Paths.LogsDirectory))
                {
                    var info = new FileInfo(launcherLogPath);
                    if (!PathEquals(SafePath.Canonical(launcherLogPath), launcherLogPath) || info.LinkTarget is not null
                        || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Observational log path is not a regular owned path.");
                    if (info.Exists) JavaTrustTree.EnsureSingleRegularFile(launcherLogPath);
                    File.AppendAllText(launcherLogPath, $"{log.Timestamp:O} [{log.Level}] {log.Message}\n");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
            { /* Observational logging cannot turn an already committed operation into a reported failure. */ }
        }
        if (LogReceived is { } received) foreach (EventHandler<LauncherLog> subscriber in received.GetInvocationList()) { try { subscriber(this, log); } catch { } }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); installCancellation?.Cancel();
        await operation.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var current in games.Values)
            {
                if (current.IndependentLog && current.Process is IDetachableProcess detachable) await detachable.DetachAsync().ConfigureAwait(false);
                else await current.Process.StopAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(gameObservers.Values).ConfigureAwait(false);
            if (sessionMonitor is not null) await sessionMonitor.ConfigureAwait(false);
            await config.DisposeAsync().ConfigureAwait(false);
        }
        finally { operation.Release(); lifetime.Dispose(); }
    }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
    private sealed class ProcessLogCollector(Action<bool, string> emit)
    {
        private readonly StringBuilder stdout = new(), stderr = new();
        private readonly object gate = new();
        public void Report(ProcessOutput output)
        {
            lock (gate)
            {
                var buffer = output.IsStandardError ? stderr : stdout; buffer.Append(output.Text);
                while (buffer.ToString().IndexOf('\n') is var index && index >= 0)
                { var line = buffer.ToString(0, index).TrimEnd('\r'); buffer.Remove(0, index + 1); emit(output.IsStandardError, line); }
                // Do not emit partial lines: a token may straddle process stream chunks.
                if (buffer.Length > 131072) { buffer.Clear(); emit(output.IsStandardError, "[overlong game log line omitted]"); }
            }
        }
        public void Flush(bool omitPartial) { lock (gate) { if (!omitPartial) { if (stdout.Length > 0) emit(false, stdout.ToString()); if (stderr.Length > 0) emit(true, stderr.ToString()); } stdout.Clear(); stderr.Clear(); } }
    }
}
