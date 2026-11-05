using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfInstanceWorldsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Window window, string name) { var button = Find<Button>(window, name); if (button.Command is { } command) { Assert.True(command.CanExecute(button.CommandParameter)); command.Execute(button.CommandParameter); } else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window); }

    [AvaloniaFact]
    public async Task WorldListDetailsAndDatapackImportUseRealFilesWithoutWritingLevelDat()
    {
        using var fixture = new Fixture();
        await fixture.OpenWorldsAsync();
        Assert.Single(fixture.Vm.WorldRows); Assert.Contains("Fixture", fixture.Vm.WorldRows[0].Name);
        Click(fixture.Window, "OpenWorldDetailsButton");
        Assert.True(fixture.Vm.IsWorldInformationPage); Assert.Equal("-1234567890123456789", Find<TextBlock>(fixture.Window, "WorldSeedText").Text);
        Assert.Equal("Fixture 世界甲", Find<TextBlock>(fixture.Window, "WorldNameText").Text);
        Assert.Contains("1.21.1", Find<TextBlock>(fixture.Window, "WorldVersionText").Text);
        Click(fixture.Window, "WorldDatapacksNavigation"); await fixture.Vm.ShowWorldDatapacksCommand.ExecutionTask!; Pump(fixture.Window);
        Assert.Empty(fixture.Vm.DatapackRows);
        var imported = await fixture.Vm.ImportWorldDatapackFileAsync(fixture.ZipPath, "one", "world", fixture.GameOne); Pump(fixture.Window);
        Assert.True(imported); Assert.Single(fixture.Vm.DatapackRows); Assert.True(File.Exists(Path.Combine(fixture.GameOne, "saves/world/datapacks/fixture.zip")));
        var list = Find<ListBox>(fixture.Window, "WorldDatapackList"); list.SelectedItem = fixture.Vm.DatapackRows[0]; Pump(fixture.Window);
        Assert.True(fixture.Vm.HasSelectedDatapacks); Assert.True(Find<Border>(fixture.Window, "WorldDatapackSelectionBar").IsVisible);
        Assert.False(Find<Button>(fixture.Window, "EnableWorldDatapacksButton").IsEnabled);
        Click(fixture.Window, "ClearWorldDatapackSelectionButton"); Assert.False(fixture.Vm.HasSelectedDatapacks);
        Assert.Equal(fixture.LevelBytes, File.ReadAllBytes(fixture.LevelOne));
        Click(fixture.Window, "BackToWorldListButton"); Assert.True(fixture.Vm.IsWorldListPage);
    }

    [AvaloniaFact]
    public async Task ServerEditorAddsEditsRemovesOnlyDraftUntilExplicitSaveAndShowsBackup()
    {
        using var fixture = new Fixture(); await fixture.OpenServersAsync();
        var original = File.ReadAllBytes(fixture.ServersOne);
        Click(fixture.Window, "AddInstanceServerButton");
        Find<TextBox>(fixture.Window, "InstanceServerNameDraft").Text = "Local draft";
        Find<TextBox>(fixture.Window, "InstanceServerAddressDraft").Text = "local.invalid:25565"; Pump(fixture.Window);
        Click(fixture.Window, "ApplyInstanceServerDraftButton");
        Assert.Equal(original, File.ReadAllBytes(fixture.ServersOne)); Assert.Equal(2, fixture.Vm.InstanceServerRows.Count); Assert.True(fixture.Vm.IsServerDraftDirty);
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot); Pump(fixture.Window); Assert.Equal(2, fixture.Vm.InstanceServerRows.Count);
        fixture.Vm.SelectedInstanceServer = fixture.Vm.InstanceServerRows[0]; fixture.Vm.EditInstanceServerCommand.Execute(null); Pump(fixture.Window);
        Find<TextBox>(fixture.Window, "InstanceServerNameDraft").Text = "Renamed fixture"; Pump(fixture.Window); Click(fixture.Window, "ApplyInstanceServerDraftButton");
        fixture.Vm.SelectedInstanceServer = fixture.Vm.InstanceServerRows[1]; fixture.Vm.RemoveInstanceServerCommand.Execute(null); Pump(fixture.Window);
        Assert.Equal(original, File.ReadAllBytes(fixture.ServersOne)); Assert.Single(fixture.Vm.InstanceServerRows);
        Click(fixture.Window, "SaveInstanceServersButton"); await fixture.Vm.SaveInstanceServersCommand.ExecutionTask!; Pump(fixture.Window);
        Assert.False(fixture.Vm.IsServerDraftDirty); Assert.True(fixture.Vm.HasServerBackup); Assert.True(File.Exists(fixture.Vm.ServerBackupPath));
        Assert.Equal(original, File.ReadAllBytes(fixture.Vm.ServerBackupPath));
        var actual = await fixture.Service.GetInstanceServersAsync("one", Ct); Assert.Equal("Renamed fixture", actual.Entries[0].Name);
        Assert.Empty(fixture.Service.Base.SavedSecretKinds);
    }

    [AvaloniaFact]
    public async Task RevisionConflictKeepsLocalDraftUntilExplicitDiscardAndReload()
    {
        using var fixture = new Fixture(); await fixture.OpenServersAsync();
        fixture.AddDraft("Kept draft", "kept.invalid");
        var external = await fixture.Service.GetInstanceServersAsync("one", Ct);
        await fixture.Service.SaveInstanceServersAsync("one", external.Revision, [new(0, "External change", "external.invalid")], Ct);
        var writes = fixture.Service.ServerSaveCalls;
        await fixture.Vm.SaveInstanceServersCommand.ExecuteAsync(null); Pump(fixture.Window);
        Assert.True(fixture.Vm.HasServerConflict); Assert.True(fixture.Vm.IsServerDraftDirty); Assert.Contains(fixture.Vm.InstanceServerRows, row => row.Name == "Kept draft");
        Assert.Equal(writes, fixture.Service.ServerSaveCalls); Assert.False(fixture.Vm.SaveInstanceServersCommand.CanExecute(null));
        await fixture.Vm.DiscardInstanceServersCommand.ExecuteAsync(null); Pump(fixture.Window);
        Assert.False(fixture.Vm.IsServerDraftDirty); Assert.False(fixture.Vm.HasServerConflict); Assert.Single(fixture.Vm.InstanceServerRows); Assert.Equal("External change", fixture.Vm.InstanceServerRows[0].Name);
    }

    [AvaloniaFact]
    public async Task LateReadUpdatesOnlyOriginalInstanceCacheAndReturningRestoresItsRows()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.WorldRead = async (id, ct) => { var rows = await fixture.Service.Store(id).GetWorldsAsync(ct); if (id == "one") { entered.TrySetResult(); await release.Task; } return rows; };
        fixture.Vm.ActivateWorldWorkspace("worlds"); Pump(fixture.Window); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot with { SelectedInstanceId = "two" }); Pump(fixture.Window);
        release.SetResult(); await fixture.Vm.RefreshWorldsCommand.ExecutionTask!; Pump(fixture.Window);
        await WaitForAsync(fixture, () => !fixture.Root.IsBusy && fixture.Vm.WorldRows.Count == 1);
        Assert.Equal("two", fixture.Vm.SelectedInstance!.Id); Assert.Equal("Fixture 世界乙", Assert.Single(fixture.Vm.WorldRows).Name);
        fixture.Service.WorldRead = null;
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot with { SelectedInstanceId = "one" }); Pump(fixture.Window);
        Assert.Equal("Fixture 世界甲", Assert.Single(fixture.Vm.WorldRows).Name);
    }

    [AvaloniaFact]
    public async Task UnreadServersActivatedDuringBusyOperationAutomaticallyLoadAfterRelease()
    {
        using var fixture = new Fixture(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = fixture.Root.RunAsync("explicit busy fixture", _ => release.Task);
        fixture.Vm.ActivateWorldWorkspace("servers"); Pump(fixture.Window);
        Assert.Empty(fixture.Vm.InstanceServerRows); Assert.Contains("尚未读取", fixture.Vm.ServerListStatus);
        release.SetResult(); await busy;
        await WaitForAsync(fixture, () => !fixture.Root.IsBusy && fixture.Vm.InstanceServerRows.Count == 1);
        Assert.Equal("Original fixture", fixture.Vm.InstanceServerRows[0].Name);
    }

    [AvaloniaFact]
    public async Task PickerTargetChangeRefusesImportAndCancelledReloadKeepsServerDraft()
    {
        using var fixture = new Fixture(); await fixture.OpenWorldsAsync(); fixture.Vm.WorldRows[0].OpenCommand.Execute(null);
        await fixture.Vm.ShowWorldDatapacksCommand.ExecuteAsync(null); Pump(fixture.Window);
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot with { SelectedInstanceId = "two" }); Pump(fixture.Window);
        Assert.False(await fixture.Vm.ImportWorldDatapackFileAsync(fixture.ZipPath, "one", "world", fixture.GameOne));
        Assert.Contains("目标已经改变", fixture.Vm.WorldWorkspaceFeedback); Assert.False(Directory.Exists(Path.Combine(fixture.GameTwo, "saves/world/datapacks")));
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot with { SelectedInstanceId = "one" }); Pump(fixture.Window); await fixture.OpenServersAsync();
        fixture.AddDraft("Cancellation keeps this", "cancel.invalid");
        fixture.Service.ServerRead = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("unreachable"); };
        fixture.Vm.DiscardInstanceServersCommand.Execute(null); Pump(fixture.Window); Assert.True(fixture.Root.IsBusy);
        fixture.Root.CancelOperationCommand.Execute(null); await fixture.Vm.DiscardInstanceServersCommand.ExecutionTask!; Pump(fixture.Window);
        Assert.True(fixture.Vm.IsServerDraftDirty); Assert.Contains(fixture.Vm.InstanceServerRows, row => row.Name == "Cancellation keeps this");
        Assert.Contains("草稿仍保留", fixture.Vm.WorldWorkspaceFeedback);
    }

    [AvaloniaFact]
    public async Task NonisolatedAndRunningGameStatesDisableRealFileWrites()
    {
        using var fixture = new Fixture(); await fixture.OpenServersAsync(); fixture.AddDraft("No unsafe save", "safe.invalid");
        var before = File.ReadAllBytes(fixture.ServersOne); fixture.Service.Isolated = false;
        fixture.Service.Base.Publish(fixture.Service.Base.Snapshot); Pump(fixture.Window);
        Assert.False(fixture.Vm.WorldWorkspaceCanRead); Assert.False(fixture.Vm.WorldWorkspaceCanWrite); Assert.Contains("读取与写入已禁用", fixture.Vm.WorldWorkspaceCapability);
        Assert.False(fixture.Vm.SaveInstanceServersCommand.CanExecute(null)); await fixture.Vm.SaveInstanceServersCommand.ExecuteAsync(null); Assert.Equal(before, File.ReadAllBytes(fixture.ServersOne));
        fixture.Service.Isolated = true; fixture.Service.Base.Publish(fixture.Service.Base.Snapshot with { GameProcessId = 42, State = "Running" }); Pump(fixture.Window);
        Assert.True(fixture.Vm.WorldWorkspaceCanRead); Assert.False(fixture.Vm.WorldWorkspaceCanWrite); Assert.False(Find<Button>(fixture.Window, "SaveInstanceServersButton").IsEffectivelyEnabled);
    }
    private static async Task WaitForAsync(Fixture fixture, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(fixture.Window); await Task.Delay(5, Ct); }
        Pump(fixture.Window); Assert.True(condition());
    }

    private sealed class Fixture : IDisposable
    {
        public WorldUiService Service { get; } = new();
        public MainWindowViewModel Root { get; }
        public InstancesPageViewModel Vm => Root.Instances;
        public Window Window { get; }
        public string GameOne { get; }
        public string GameTwo { get; }
        public string LevelOne => Path.Combine(GameOne, "saves/world/level.dat");
        public string ServersOne => Path.Combine(GameOne, "servers.dat");
        public byte[] LevelBytes { get; }
        public string ZipPath { get; }
        public Fixture()
        {
            var directory = Path.Combine(Service.Paths.InstancesDirectory, "world-ui-" + Guid.NewGuid().ToString("N"));
            GameOne = Path.Combine(directory, "one"); GameTwo = Path.Combine(directory, "two");
            foreach (var (path, name) in new[] { (GameOne, "Fixture 世界甲"), (GameTwo, "Fixture 世界乙") })
            {
                Directory.CreateDirectory(Path.Combine(path, "saves/world"));
                var data = new NbtTag(NbtType.Compound, "Data", new NbtTag[] { new(NbtType.String, "LevelName", name), new(NbtType.Long, "RandomSeed", -1234567890123456789L), new(NbtType.Int, "GameType", 1), new(NbtType.Int, "DataVersion", 3955), new(NbtType.Long, "LastPlayed", 1700000000000L), new(NbtType.Compound, "Version", new NbtTag[] { new(NbtType.String, "Name", "1.21.1") }) });
                File.WriteAllBytes(Path.Combine(path, "saves/world/level.dat"), BoundedNbt.Write(new(new(NbtType.Compound, "", new[] { data }), NbtCompression.Gzip)));
                var server = new NbtTag(NbtType.Compound, "", new NbtTag[] { new(NbtType.String, "name", "Original fixture"), new(NbtType.String, "ip", "original.invalid") });
                File.WriteAllBytes(Path.Combine(path, "servers.dat"), BoundedNbt.Write(new(new(NbtType.Compound, "", new NbtTag[] { new(NbtType.List, "servers", new NbtList(NbtType.Compound, new[] { server })) }))));
            }
            LevelBytes = File.ReadAllBytes(LevelOne);
            ZipPath = Path.Combine(directory, "fixture.zip");
            using (var file = File.Create(ZipPath)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            { var entry = zip.CreateEntry("pack.mcmeta"); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write("{\"pack\":{\"pack_format\":48,\"description\":\"UI fixture\"}}"); }
            Service.Base.Publish(Service.Base.Snapshot with { Instances = [new("one", "Fixture one", "1.21.1", GameOne), new("two", "Fixture two", "1.21.1", GameTwo)], SelectedInstanceId = "one" });
            Root = new(Service, new FakeExternalLauncher()); Vm.InitializeWorldWorkspace(); Vm.NotifyWorldWorkspace(); Vm.ShowInstanceSettingsCommand.Execute(null);
            Window = new() { Width = 976, Height = 588, Content = new InstanceWorldsView { DataContext = Vm } }; Window.Show(); Pump(Window);
        }
        public async Task OpenWorldsAsync() { Vm.ActivateWorldWorkspace("worlds"); Pump(Window); if (Vm.RefreshWorldsCommand.ExecutionTask is { } task) await task; Pump(Window); }
        public async Task OpenServersAsync() { Vm.ActivateWorldWorkspace("servers"); Pump(Window); if (Vm.RefreshInstanceServersCommand.ExecutionTask is { } task) await task; Pump(Window); }
        public void AddDraft(string name, string address) { Vm.AddInstanceServerCommand.Execute(null); Vm.ServerNameDraft = name; Vm.ServerAddressDraft = address; Vm.ApplyInstanceServerDraftCommand.Execute(null); Pump(Window); }
        public void Dispose() { Window.Close(); Root.Dispose(); }
    }

    // Explicit UI protocol decorator: real bounded file store, with controllable delayed reads for stale-result/cancel gates.
    private sealed class WorldUiService : ILauncherService, IInstanceWorldService
    {
        public FakeLauncherService Base { get; } = new();
        public bool Isolated = true;
        public int ServerSaveCalls;
        public Func<string, CancellationToken, Task<IReadOnlyList<InstanceWorldSummary>>>? WorldRead;
        public Func<string, CancellationToken, Task<InstanceServerList>>? ServerRead;
        public LauncherSnapshot Snapshot => Base.Snapshot;
        public IAppPaths Paths => new IsolatedPaths(Base.Paths, Isolated);
        public IPlatformCapabilities Capabilities => Base.Capabilities;
        public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => Base.SnapshotChanged += value; remove => Base.SnapshotChanged -= value; }
        public event EventHandler<LauncherLog>? LogReceived { add => Base.LogReceived += value; remove => Base.LogReceived -= value; }
        public InstanceWorldStore Store(string id) { Assert.True(Isolated); Assert.Equal(Snapshot.SelectedInstanceId, id); return new(Paths.DataDirectory, Snapshot.Instances.Single(instance => instance.Id == id).GameDirectory); }
        public Task<InstanceServerList> GetInstanceServersAsync(string id, CancellationToken ct = default) => ServerRead?.Invoke(id, ct) ?? Store(id).GetServersAsync(ct);
        public Task<InstanceServerList> SaveInstanceServersAsync(string id, string revision, IReadOnlyList<InstanceServerEdit> entries, CancellationToken ct = default) { ServerSaveCalls++; return Store(id).SaveServersAsync(revision, entries, ct); }
        public Task<IReadOnlyList<InstanceWorldSummary>> GetInstanceWorldsAsync(string id, CancellationToken ct = default) => WorldRead?.Invoke(id, ct) ?? Store(id).GetWorldsAsync(ct);
        public Task<IReadOnlyList<WorldDatapackFile>> GetWorldDatapacksAsync(string id, string world, CancellationToken ct = default) => Store(id).GetDatapacksAsync(world, ct);
        public Task<WorldDatapackFile> ImportWorldDatapackAsync(string id, string world, string zip, CancellationToken ct = default) => Store(id).ImportDatapackAsync(world, zip, ct);
        public Task InitializeAsync(CancellationToken ct = default) => Base.InitializeAsync(ct);
        public Task<GameInstance> CreateInstanceAsync(string name, string versionId, CancellationToken ct = default) => Base.CreateInstanceAsync(name, versionId, ct);
        public Task<GameInstance> CreateFabricInstanceAsync(string name, string gameVersionId, string loaderVersion, CancellationToken ct = default) => Base.CreateFabricInstanceAsync(name, gameVersionId, loaderVersion, ct);
        public Task SelectInstanceAsync(string instanceId, CancellationToken ct = default) => Base.SelectInstanceAsync(instanceId, ct);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken ct = default) => Base.DiscoverJavaAsync(ct);
        public Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string javaExecutable, CancellationToken ct = default) => Base.InspectJavaTrustAsync(javaExecutable, ct);
        public Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string javaExecutable, CancellationToken ct = default) => Base.CreateRepairedJavaCopyAsync(javaExecutable, ct);
        public Task InstallInstanceAsync(string instanceId, CancellationToken ct = default) => Base.InstallInstanceAsync(instanceId, ct);
        public Task<LaunchPlan> BuildLaunchPlanAsync(string instanceId, CancellationToken ct = default) => Base.BuildLaunchPlanAsync(instanceId, ct);
        public Task LaunchAsync(string instanceId, string serverId = "none", CancellationToken ct = default) => Base.LaunchAsync(instanceId, serverId, ct);
        public Task StopGameAsync(CancellationToken ct = default) => Base.StopGameAsync(ct);
        public Task CancelDownloadAsync(string taskId, CancellationToken ct = default) => Base.CancelDownloadAsync(taskId, ct);
        public Task RetryDownloadAsync(string taskId, CancellationToken ct = default) => Base.RetryDownloadAsync(taskId, ct);
        public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default) => Base.SaveSettingsAsync(settings, ct);
        public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken ct = default) => Base.SaveConnectionSecretAsync(kind, secret, ct);
        public Task<AccountProfile> AddOfflineAccountAsync(string name, CancellationToken ct = default) => Base.AddOfflineAccountAsync(name, ct);
        public Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken ct = default) => Base.LoginMicrosoftAsync(challenge, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string username, string password, CancellationToken ct = default) => Base.LoginYggdrasilAsync(server, username, password, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string username, string password, Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> select, CancellationToken ct = default) => Base.LoginYggdrasilWithProfileSelectionAsync(server, username, password, select, ct);
        public Task RefreshAccountAsync(string id, CancellationToken ct = default) => Base.RefreshAccountAsync(id, ct);
        public Task SelectAccountAsync(string id, CancellationToken ct = default) => Base.SelectAccountAsync(id, ct);
        public Task RemoveAccountAsync(string id, CancellationToken ct = default) => Base.RemoveAccountAsync(id, ct);
        public Task RefreshServersAsync(CancellationToken ct = default) => Base.RefreshServersAsync(ct);
        public IReadOnlyList<LauncherLog> GetRecentLogs() => Base.GetRecentLogs();
        public ValueTask DisposeAsync() => Base.DisposeAsync();
    }
    private sealed class IsolatedPaths(IAppPaths source, bool isolated) : IAppPaths
    {
        public string DataDirectory => source.DataDirectory;
        public string SharedDataDirectory => source.SharedDataDirectory;
        public string LocalDataDirectory => source.LocalDataDirectory;
        public string CacheDirectory => source.CacheDirectory;
        public string LogsDirectory => source.LogsDirectory;
        public string RuntimesDirectory => source.RuntimesDirectory;
        public string InstancesDirectory => source.InstancesDirectory;
        public string TempDirectory => source.TempDirectory;
        public string ResourcesDirectory => source.ResourcesDirectory;
        public string LockFile => source.LockFile;
        public string CredentialScope => source.CredentialScope;
        public bool IsIsolated => isolated;
    }
}
