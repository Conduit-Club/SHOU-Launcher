using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfWorldEditingUiTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window window, string name) { var button = Find<Button>(window, name); if (button.Command is { } command) { Assert.True(command.CanExecute(button.CommandParameter)); command.Execute(button.CommandParameter); } else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window); }

    [AvaloniaFact]
    public async Task BoundWorldSettingsRemainDraftUntilExplicitSaveAndShowRealBackup()
    {
        using var f = new Fixture(); await f.OpenDetailsAsync(); var before = File.ReadAllBytes(f.LevelOne);
        Assert.Equal("16 / -60 / -32", Find<TextBlock>(f.Window, "WorldSpawnText").Text); Assert.Contains("世界运行时间", f.Vm.WorldRuntimeText);
        Find<ComboBox>(f.Window, "WorldAllowCommandsSelector").SelectedItem = f.Vm.WorldPermissionChoices[1];
        Find<ComboBox>(f.Window, "WorldDifficultySelector").SelectedItem = f.Vm.WorldDifficultyChoices[3];
        Find<CheckBox>(f.Window, "WorldDifficultyLockedCheckBox").IsChecked = true; Pump(f.Window);
        Assert.True(f.Vm.IsWorldSettingsDirty); Assert.Equal(before, File.ReadAllBytes(f.LevelOne)); Assert.Equal(0, f.Service.MetadataSaves);
        Click(f.Window, "SaveWorldSettingsButton"); await f.Vm.SaveWorldSettingsCommand.ExecutionTask!; Pump(f.Window);
        Assert.False(f.Vm.IsWorldSettingsDirty); Assert.Equal(1, f.Service.MetadataSaves); Assert.True(f.Vm.HasWorldEditBackup); Assert.Equal(before, File.ReadAllBytes(f.Vm.WorldEditBackupPath));
        var actual = await f.Service.Store("one").GetEditStateAsync("world"); Assert.True(actual.Values.AllowCommands); Assert.Equal(3, actual.Values.Difficulty); Assert.True(actual.Values.DifficultyLocked);
        Assert.False(Find<ComboBox>(f.Window, "WorldDifficultySelector").IsEnabled); Assert.Equal(f.LevelTwoBytes, File.ReadAllBytes(f.LevelTwo));
    }
    [AvaloniaFact]
    public async Task MetadataConflictKeepsDraftAndExplicitDiscardReloadsRealValues()
    {
        using var f = new Fixture(); await f.OpenDetailsAsync(); f.Vm.SelectedWorldPermission = f.Vm.WorldPermissionChoices[1];
        var external = await f.Service.Store("one").GetEditStateAsync("world"); await f.Service.Store("one").SaveMetadataAsync("world", external.Revision, new(false, 1, false));
        var changed = File.ReadAllBytes(f.LevelOne); await f.Vm.SaveWorldSettingsCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.True(f.Vm.HasWorldSettingsConflict); Assert.True(f.Vm.IsWorldSettingsDirty); Assert.True(f.Vm.SelectedWorldPermission.Value); Assert.Equal(changed, File.ReadAllBytes(f.LevelOne)); Assert.False(f.Vm.SaveWorldSettingsCommand.CanExecute(null));
        await f.Vm.DiscardWorldSettingsCommand.ExecuteAsync(null); Pump(f.Window);
        Assert.False(f.Vm.HasWorldSettingsConflict); Assert.False(f.Vm.IsWorldSettingsDirty); Assert.False(f.Vm.SelectedWorldPermission.Value); Assert.Equal(1, f.Vm.SelectedWorldDifficulty.Value);
    }
    [AvaloniaFact]
    public async Task CancelledDiscardRetainsUnsavedWorldSettings()
    {
        using var f = new Fixture(); await f.OpenDetailsAsync(); f.Vm.SelectedWorldPermission = f.Vm.WorldPermissionChoices[1]; var original = File.ReadAllBytes(f.LevelOne);
        var lastRead = await f.Service.Store("one").GetEditStateAsync("world"); var delayed = new TaskCompletionSource<WorldEditState>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.EditRead = (_, _, _) => delayed.Task; // Deliberately ignores cancellation, as an already completed file read may do.
        var pending = f.Vm.DiscardWorldSettingsCommand.ExecuteAsync(null); f.Root.CancelOperationCommand.Execute(null); delayed.SetResult(lastRead); await pending; Pump(f.Window);
        Assert.True(f.Vm.IsWorldSettingsDirty); Assert.True(f.Vm.SelectedWorldPermission.Value); Assert.Equal(original, File.ReadAllBytes(f.LevelOne)); Assert.Equal(0, f.Service.MetadataSaves);
        Assert.True(f.Vm.CanEditWorldMetadata); Assert.True(f.Vm.SaveWorldSettingsCommand.CanExecute(null));
    }
    [AvaloniaFact]
    public async Task DatapackSelectionActuallyEnablesAndDisablesNbtWhilePreservingMetadataDraft()
    {
        using var f = new Fixture(); await f.OpenDetailsAsync(); f.Vm.SelectedWorldPermission = f.Vm.WorldPermissionChoices[1];
        await f.Vm.ShowWorldDatapacksCommand.ExecuteAsync(null); Pump(f.Window); Assert.Single(f.Vm.DatapackRows);
        var row = f.Vm.DatapackRows[0]; Assert.Contains("Synthetic UI pack", row.Description); Assert.False(row.Details!.Enabled);
        Find<ListBox>(f.Window, "WorldDatapackList").SelectedItem = row; Pump(f.Window); Assert.True(Find<Button>(f.Window, "EnableWorldDatapacksButton").IsEnabled);
        Click(f.Window, "EnableWorldDatapacksButton"); await f.Vm.EnableWorldDatapacksCommand.ExecutionTask!; Pump(f.Window);
        Assert.True(f.Vm.DatapackRows[0].Details!.Enabled); Assert.True(f.Vm.IsWorldSettingsDirty); Assert.True(f.Vm.SelectedWorldPermission.Value); Assert.True(f.Vm.HasWorldEditBackup);
        var actual = await f.Service.Store("one").GetEditStateAsync("world"); Assert.True(actual.Datapacks.Single().Enabled); Assert.False(actual.Values.AllowCommands);
        await f.Vm.DisableWorldDatapacksCommand.ExecuteAsync(null); Pump(f.Window); Assert.False((await f.Service.Store("one").GetEditStateAsync("world")).Datapacks.Single().Enabled);
        f.Vm.ShowWorldInformationCommand.Execute(null); await f.Vm.SaveWorldSettingsCommand.ExecuteAsync(null); Pump(f.Window); Assert.True((await f.Service.Store("one").GetEditStateAsync("world")).Values.AllowCommands);
        Assert.Equal(2, f.Service.DatapackSaves); Assert.Equal(1, f.Service.MetadataSaves);
    }
    [AvaloniaFact]
    public async Task BusyOwnerDefersUnreadWorldSettingsAndLoadsAfterBusyOperationCompletes()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = f.Root.RunAsync("Explicit busy fixture", _ => gate.Task, true); f.Vm.WorldRows.Single().OpenCommand.Execute(null); Pump(f.Window);
        Assert.Equal(0, f.Service.EditReads); Assert.False(f.Vm.CanEditWorldMetadata);
        gate.SetResult(); await busy; Pump(f.Window); if (f.Vm.RefreshWorldEditCommand.ExecutionTask is { } task) await task; Pump(f.Window);
        Assert.Equal(1, f.Service.EditReads); Assert.True(f.Vm.CanEditWorldMetadata); Assert.Equal("16 / -60 / -32", f.Vm.WorldSpawnText);
    }
    [AvaloniaFact]
    public async Task OldWorldEditReadCannotPopulateNewInstanceAndRunningGameDisablesWrites()
    {
        using var f = new Fixture(); await f.OpenWorldListAsync(); var old = await f.Service.Store("one").GetEditStateAsync("world");
        var pending = new TaskCompletionSource<WorldEditState>(TaskCreationOptions.RunContinuationsAsynchronously); f.Service.EditRead = (_, _, _) => pending.Task;
        f.Vm.WorldRows.Single().OpenCommand.Execute(null); Pump(f.Window); var read = f.Vm.RefreshWorldEditCommand.ExecutionTask!;
        f.Service.Base.Publish(f.Service.Snapshot with { SelectedInstanceId = "two" }); pending.SetResult(old); await read; Pump(f.Window);
        Assert.Equal("two", f.Vm.SelectedInstance!.Id); Assert.Equal("未知", f.Vm.WorldSpawnText); Assert.False(f.Vm.IsWorldSettingsDirty);
        f.Service.EditRead = null; await f.OpenDetailsAsync(); f.Service.Base.Publish(f.Service.Snapshot with { State = "Running", GameProcessId = 54321 }); Pump(f.Window);
        Assert.False(f.Vm.CanEditWorldMetadata); Assert.False(f.Vm.SaveWorldSettingsCommand.CanExecute(null)); var original = File.ReadAllBytes(f.LevelTwo);
        f.Vm.SelectedWorldPermission = f.Vm.WorldPermissionChoices[1]; Assert.False(f.Vm.IsWorldSettingsDirty); Assert.Equal(original, File.ReadAllBytes(f.LevelTwo));
    }
    [AvaloniaFact]
    public async Task HiddenNullableWorldFieldsAndDatapackStateChangesHaveNoBindingWarnings()
    {
        var previous = Logger.Sink; var failures = new BindingFailures(); Logger.Sink = failures;
        try
        {
            using var f = new Fixture(); await f.OpenDetailsAsync(); await f.Vm.ShowWorldDatapacksCommand.ExecuteAsync(null); Pump(f.Window);
            f.Vm.DatapackSearchText = "Synthetic UI"; Pump(f.Window); Assert.Single(f.Vm.DatapackRows); f.Vm.BackFromWorldCommand.Execute(null); Pump(f.Window);
            Assert.Empty(failures.Errors);
        }
        finally { Logger.Sink = previous; }
    }
    private sealed class Fixture : IDisposable
    {
        public WorldEditUiService Service { get; } = new();
        public MainWindowViewModel Root { get; }
        public InstancesPageViewModel Vm => Root.Instances;
        public Window Window { get; }
        public string LevelOne { get; }
        public string LevelTwo { get; }
        public byte[] LevelTwoBytes { get; }
        public Fixture()
        {
            var basis = Path.Combine(Service.Paths.InstancesDirectory, "world-edit-ui-" + Guid.NewGuid().ToString("N"));
            var one = Path.Combine(basis, "one"); var two = Path.Combine(basis, "two");
            foreach (var (directory, name) in new[] { (one, "World one fixture"), (two, "World two fixture") })
            {
                Directory.CreateDirectory(Path.Combine(directory, "saves/world/datapacks")); var data = new NbtTag(NbtType.Compound, "Data", new NbtTag[]
                { new(NbtType.String, "LevelName", name), new(NbtType.Int, "DataVersion", 3955), new(NbtType.Compound, "Version", new NbtTag[] { new(NbtType.String, "Name", "1.21.1") }),
                  new(NbtType.Byte, "allowCommands", (sbyte)0), new(NbtType.Byte, "Difficulty", (sbyte)2), new(NbtType.Byte, "DifficultyLocked", (sbyte)0), new(NbtType.Byte, "hardcore", (sbyte)0),
                  new(NbtType.Int, "SpawnX", 16), new(NbtType.Int, "SpawnY", -60), new(NbtType.Int, "SpawnZ", -32), new(NbtType.Long, "Time", 12000L),
                  new(NbtType.Compound, "DataPacks", new NbtTag[] { new(NbtType.List, "Enabled", new NbtList(NbtType.String, [new(NbtType.String, "", "vanilla")])), new(NbtType.List, "Disabled", new NbtList(NbtType.String, [new(NbtType.String, "", "file/fixture.zip")])) }) });
                File.WriteAllBytes(Path.Combine(directory, "saves/world/level.dat"), BoundedNbt.Write(new(new(NbtType.Compound, "", new[] { data }), NbtCompression.Gzip)));
                using var output = File.Create(Path.Combine(directory, "saves/world/datapacks/fixture.zip")); using var zip = new ZipArchive(output, ZipArchiveMode.Create); using var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open(), new UTF8Encoding(false)); writer.Write("{\"pack\":{\"pack_format\":48,\"description\":\"Synthetic UI pack\"}}");
            }
            LevelOne = Path.Combine(one, "saves/world/level.dat"); LevelTwo = Path.Combine(two, "saves/world/level.dat"); LevelTwoBytes = File.ReadAllBytes(LevelTwo);
            Service.Base.Publish(Service.Snapshot with { Instances = [new("one", "One", "1.21.1", one), new("two", "Two", "1.21.1", two)], SelectedInstanceId = "one" });
            Root = new(Service, new FakeExternalLauncher()); Vm.InitializeWorldWorkspace(); Vm.NotifyWorldWorkspace(); Vm.ShowInstanceSettingsCommand.Execute(null);
            Window = new() { Width = 976, Height = 588, Content = new InstanceWorldsView { DataContext = Vm } }; Window.Show(); Pump(Window);
        }
        public async Task OpenWorldListAsync() { Vm.ActivateWorldWorkspace("worlds"); Pump(Window); if (Vm.RefreshWorldsCommand.ExecutionTask is { } task) await task; Pump(Window); }
        public async Task OpenDetailsAsync()
        {
            await OpenWorldListAsync(); if (Vm.IsWorldListPage) Vm.WorldRows.Single().OpenCommand.Execute(null); Pump(Window);
            if (Vm.RefreshWorldEditCommand.ExecutionTask is { } task) await task; Pump(Window);
        }
        public void Dispose() { Window.Close(); Root.Dispose(); }
    }
    private sealed class BindingFailures : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string template) { if (IsEnabled(level, area)) Errors.Add(template); }
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) { if (IsEnabled(level, area)) Errors.Add(template + " " + string.Join(" | ", values)); }
    }
    private sealed class WorldEditUiService : ILauncherService, IInstanceWorldService, IInstanceWorldEditService
    {
        public FakeLauncherService Base { get; } = new();
        public IAppPaths Paths => Base.Paths;
        public LauncherSnapshot Snapshot => Base.Snapshot;
        public IPlatformCapabilities Capabilities => Base.Capabilities;
        public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => Base.SnapshotChanged += value; remove => Base.SnapshotChanged -= value; }
        public event EventHandler<LauncherLog>? LogReceived { add => Base.LogReceived += value; remove => Base.LogReceived -= value; }
        public int EditReads, MetadataSaves, DatapackSaves;
        public Func<string, string, CancellationToken, Task<WorldEditState>>? EditRead;
        public InstanceWorldStore Store(string id) { Assert.Equal(Snapshot.SelectedInstanceId, id); return new(Paths.DataDirectory, Snapshot.Instances.Single(i => i.Id == id).GameDirectory); }
        public Task<WorldEditState> GetWorldEditStateAsync(string id, string world, CancellationToken ct = default) { EditReads++; return EditRead?.Invoke(id, world, ct) ?? Store(id).GetEditStateAsync(world, ct); }
        public Task<WorldEditReceipt> SaveWorldMetadataAsync(string id, string world, string revision, WorldMetadataEdit edit, CancellationToken ct = default) { MetadataSaves++; return Store(id).SaveMetadataAsync(world, revision, edit, ct); }
        public Task<WorldEditReceipt> SetWorldDatapacksEnabledAsync(string id, string world, string revision, IReadOnlyList<WorldDatapackSelection> packs, bool enabled, CancellationToken ct = default) { DatapackSaves++; return Store(id).SetDatapacksEnabledAsync(world, revision, packs, enabled, ct); }
        public Task<InstanceServerList> GetInstanceServersAsync(string id, CancellationToken ct = default) => Store(id).GetServersAsync(ct);
        public Task<InstanceServerList> SaveInstanceServersAsync(string id, string revision, IReadOnlyList<InstanceServerEdit> entries, CancellationToken ct = default) => Store(id).SaveServersAsync(revision, entries, ct);
        public Task<IReadOnlyList<InstanceWorldSummary>> GetInstanceWorldsAsync(string id, CancellationToken ct = default) => Store(id).GetWorldsAsync(ct);
        public Task<IReadOnlyList<WorldDatapackFile>> GetWorldDatapacksAsync(string id, string world, CancellationToken ct = default) => Store(id).GetDatapacksAsync(world, ct);
        public Task<WorldDatapackFile> ImportWorldDatapackAsync(string id, string world, string path, CancellationToken ct = default) => Store(id).ImportDatapackAsync(world, path, ct);
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
}
