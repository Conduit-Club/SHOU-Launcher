using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class AccountAppearanceUiTests
{
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Click(Window window, string name) { var button = Find<Button>(window, name); if (button.Command is { } command) { Assert.True(command.CanExecute(button.CommandParameter)); command.Execute(button.CommandParameter); } else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(window); }
    private static MenuItem Menu(Window window, string name) => ((MenuFlyout)Find<Button>(window, "AccountSkinMenuButton").Flyout!).Items.OfType<MenuItem>().Single(item => item.Name == name);
    private static void OpenMenu(Window window) { var button = Find<Button>(window, "AccountSkinMenuButton"); button.Flyout!.ShowAt(button); Pump(window); }

    [AvaloniaFact]
    public async Task LocalPickerCancelPreservesSkinAndConfirmedSelectionActuallyPersistsCapturedModel()
    {
        using var fixture = await Fixture.OpenAsync(false); var vm = fixture.Vm;
        vm.SelectedLocalSkinModel = vm.SkinModelChoices[1]; var cancelled = vm.CaptureLocalSkinTarget()!;
        Assert.False(await vm.SavePickedSkinAsync(null, cancelled)); vm.EndLocalSkinPicker(cancelled); Assert.Equal(0, fixture.Service.SaveCalls);
        var target = vm.CaptureLocalSkinTarget()!; Assert.False(vm.CanChooseLocalSkin);
        Assert.True(await vm.SavePickedSkinAsync(fixture.PngPath, target)); vm.EndLocalSkinPicker(target); Pump(fixture.Window);
        Assert.Equal(1, fixture.Service.SaveCalls); Assert.Equal(AccountSkinModel.Slim, vm.AccountSkinModel);
        var saved = await new AccountAppearanceStore(fixture.Service.Paths.LocalDataDirectory).LoadOfflineAsync(fixture.Service.Snapshot.Accounts[0]);
        Assert.Equal(fixture.PngBytes, saved.Skin!.Png.ToArray()); Assert.Equal(AccountSkinModel.Slim, saved.Model);
        Assert.Same(vm.AccountSkin, Find<AccountSkinAvatarControl>(fixture.Window, "SelectedAccountAvatar").Skin);
        OpenMenu(fixture.Window); Assert.True(Menu(fixture.Window, "SaveAccountSkinMenuItem").IsEnabled); Assert.True(Menu(fixture.Window, "PreviewAccountSkinMenuItem").IsEnabled);
    }
    [AvaloniaFact]
    public async Task PickerReturnAfterSwitchingAccountsCannotWriteEitherAccount()
    {
        using var fixture = await Fixture.OpenAsync(false); var vm = fixture.Vm; var target = vm.CaptureLocalSkinTarget()!;
        vm.SelectedAccount = fixture.Service.Snapshot.Accounts[1];
        Assert.False(await vm.SavePickedSkinAsync(fixture.PngPath, target)); vm.EndLocalSkinPicker(target);
        Assert.Equal(0, fixture.Service.SaveCalls); Assert.Contains("角色已改变", fixture.Root.ErrorMessage);
        Assert.Null((await fixture.Service.Store.LoadOfflineAsync(fixture.Service.Snapshot.Accounts[0])).Skin);
        Assert.Null((await fixture.Service.Store.LoadOfflineAsync(fixture.Service.Snapshot.Accounts[1])).Skin);
    }
    [AvaloniaFact]
    public async Task StaleUncancellableReadCannotReplaceNewlySelectedAccount()
    {
        using var fixture = await Fixture.OpenAsync(); var vm = fixture.Vm; var first = await fixture.Service.Store.LoadOfflineAsync(fixture.Service.Snapshot.Accounts[0]);
        var pending = new TaskCompletionSource<AccountAppearance>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Read = (id, _, _) => id == first.AccountId ? pending.Task : fixture.Service.Store.LoadOfflineAsync(fixture.Service.Snapshot.Accounts.Single(a => a.Id == id));
        var old = vm.ReadAccountAppearanceAsync(true); Assert.True(vm.IsAccountAppearanceBusy);
        fixture.Service.Base.Publish(fixture.Service.Snapshot with { SelectedAccountId = "offline:second" });
        await vm.ReadAccountAppearanceAsync(false); pending.SetResult(first); await old; Pump(fixture.Window);
        Assert.Equal("offline:second", vm.SelectedChannelAccount!.Id); Assert.False(vm.HasAccountSkin); Assert.Empty(vm.AccountCapes);
    }
    [AvaloniaFact]
    public async Task CancelledRefreshKeepsPreviouslyLoadedTextureAndDoesNotRescheduleRemoteRead()
    {
        using var fixture = await Fixture.OpenAsync(); var vm = fixture.Vm; var skin = vm.AccountSkin; var calls = 0;
        fixture.Service.Read = async (_, _, ct) => { calls++; await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        var pending = vm.ReadAccountAppearanceAsync(true); vm.CancelAppearanceReadCommand.Execute(null); await pending; Pump(fixture.Window);
        Assert.Same(skin, vm.AccountSkin); Assert.Contains("取消", vm.AccountAppearanceStatus); Assert.Equal(1, calls); Assert.False(vm.IsAccountAppearanceBusy);
    }
    [AvaloniaFact]
    public async Task PreviewOverlayFitsOwnerAndActualPointerDragWheelResetAndEscapeWork()
    {
        using var fixture = await Fixture.OpenAsync(); Click(fixture.Window, "AccountAvatarPreviewButton");
        var preview = Find<AccountSkinPreviewControl>(fixture.Window, "AccountSkinPreview"); var card = Find<Border>(fixture.Window, "AccountAppearanceCard");
        Assert.True(card.Bounds.Width is > 500 and <= 620, $"Card={card.Bounds}; preview={preview.Bounds}; overlay={Find<AccountAppearanceOverlay>(fixture.Window, "AccountAppearanceModal").Bounds}");
        Assert.True(preview.Bounds.Height > 200, $"Preview={preview.Bounds}; card={card.Bounds}"); Assert.NotEmpty(preview.Project(preview.Bounds.Size));
        fixture.Window.Width = 880; fixture.Window.Height = 560; Pump(fixture.Window);
        Assert.Equal(880, Find<AccountAppearanceOverlay>(fixture.Window, "AccountAppearanceModal").Bounds.Width);
        foreach (var name in new[] { "CloseAccountAppearanceButton", "ResetAccountPreviewButton", "RefreshAccountPreviewButton" })
        { var control = Find<Button>(fixture.Window, name); var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), fixture.Window)!.Value; Assert.InRange(bottomRight.X, 0, 880); Assert.InRange(bottomRight.Y, 0, 560); }
        fixture.Window.Width = 976; fixture.Window.Height = 640; Pump(fixture.Window);
        var center = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), fixture.Window)!.Value; var yaw = preview.Yaw;
        fixture.Window.MouseDown(center, MouseButton.Left); fixture.Window.MouseMove(center + new Vector(64, 10)); fixture.Window.MouseUp(center + new Vector(64, 10), MouseButton.Left); Pump(fixture.Window);
        Assert.NotEqual(yaw, preview.Yaw); fixture.Window.MouseWheel(center, new Vector(0, 2)); Assert.True(preview.Zoom > 1);
        Find<CheckBox>(fixture.Window, "PreviewOuterLayerCheckBox").IsChecked = false; Pump(fixture.Window); Assert.DoesNotContain(preview.Project(preview.Bounds.Size), p => p.Part == "hat");
        Click(fixture.Window, "ResetAccountPreviewButton"); Assert.Equal(-.35, preview.Yaw, 4); Assert.Equal(1, preview.Zoom);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(fixture.Window); Assert.False(fixture.Vm.IsAccountAppearanceDialogOpen); Assert.DoesNotContain(fixture.Window.GetVisualDescendants(), c => c is AccountAppearanceOverlay);
    }
    [AvaloniaFact]
    public async Task CapeListShowsProtocolDataSelectionOnlyChangesPreviewAndWritesUseOfficialWebsite()
    {
        var service = new AppearanceUiService(); var profile = new AccountProfile("microsoft:fixture", AccountKind.Microsoft, "在线夹具", new string('a', 32));
        service.Base.Publish(service.Snapshot with { Accounts = [profile], SelectedAccountId = profile.Id });
        var skin = AccountAppearancePng.Decode(Png()); var cape = AccountAppearancePng.Decode(Png(32), true);
        var online = new AccountAppearance(profile.Id, AccountSkinModel.Slim, skin, [new("cape-A", "夹具披风甲", true, cape), new("cape-B", "夹具披风乙", false, cape)], false, AccountAppearanceClient.ManagementUri(profile), "在线修改请前往官网。", true);
        service.Read = (_, remote, _) => Task.FromResult(remote ? online : AccountAppearanceClient.Unread(profile));
        using var fixture = await Fixture.OpenAsync(service: service); await fixture.Vm.RefreshAppearanceCommand.ExecuteAsync(null); Pump(fixture.Window);
        OpenMenu(fixture.Window); Menu(fixture.Window, "ChangeAccountCapeMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Find<Button>(fixture.Window, "AccountSkinMenuButton").Flyout!.Hide(); Pump(fixture.Window);
        var list = Find<ListBox>(fixture.Window, "AccountCapeList"); Assert.Equal(2, list.ItemCount); list.SelectedItem = fixture.Vm.AccountCapes[1]; Pump(fixture.Window);
        Assert.Equal("cape-B", fixture.Vm.SelectedPreviewCape!.Id); Assert.True(fixture.Vm.AccountCapes[0].IsActive); Assert.False(fixture.Vm.AccountCapes[1].IsActive);
        Assert.Contains("仅改变预览", fixture.Vm.AccountCapeSummary); Click(fixture.Window, "ManageAccountCapeWebsiteButton");
        Assert.Equal("https://www.minecraft.net/en-us/msaprofile/mygames/editskin", fixture.External.OpenedUris.Single().AbsoluteUri); Assert.Equal(0, service.SaveCalls);
    }
    [AvaloniaFact]
    public async Task EmptySkinDialogsAndSelectionChangesProduceNoBindingWarningsOrLeakedOverlay()
    {
        var previous = Logger.Sink; var sink = new BindingFailures(); Logger.Sink = sink;
        try
        {
            using var fixture = await Fixture.OpenAsync(false); OpenMenu(fixture.Window); Menu(fixture.Window, "ChangeAccountSkinMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Find<Button>(fixture.Window, "AccountSkinMenuButton").Flyout!.Hide(); Pump(fixture.Window);
            Assert.True(fixture.Vm.IsLocalSkinPage); Assert.True(Find<Button>(fixture.Window, "PickOfflineSkinButton").IsEnabled);
            fixture.Vm.SelectedAccount = fixture.Service.Snapshot.Accounts[1]; Pump(fixture.Window);
            Assert.False(fixture.Vm.IsAccountAppearanceDialogOpen); Assert.DoesNotContain(fixture.Window.GetVisualDescendants(), c => c is AccountAppearanceOverlay);
            Assert.Empty(sink.Errors);
        }
        finally { Logger.Sink = previous; }
    }
    [AvaloniaFact]
    public void CuboidUvUsesDifferentFrontBackTexelsAndCorrectClassicSlimArmWidths()
    {
        var preview = new AccountSkinPreviewControl { Skin = AccountAppearancePng.Decode(Png()), ShowOuterLayer = false };
        preview.Rotate(.35, .08); var front = preview.Project(new Size(400, 400));
        Assert.Contains(front, p => p.Part == "head" && p.Color.R > 100 && p.Color.G == 0 && p.Color.B == 0);
        Assert.Equal(48, front.Count(p => p.Part == "leftArm"));
        preview.Model = AccountSkinModel.Slim; Assert.Equal(36, preview.Project(new Size(400, 400)).Count(p => p.Part == "leftArm"));
        preview.Rotate(Math.PI, 0); Assert.Contains(preview.Project(new Size(400, 400)), p => p.Part == "head" && p.Color.B > 100 && p.Color.R == 0 && p.Color.G == 0);
        preview.ShowOuterLayer = true; Assert.Contains(preview.Project(new Size(400, 400)), p => p.Part == "hat");
        preview.Cape = AccountAppearancePng.Decode(Png(32), true); Assert.Contains(preview.Project(new Size(400, 400)), p => p.Part == "cape");
        preview.ShowCape = false; Assert.DoesNotContain(preview.Project(new Size(400, 400)), p => p.Part == "cape");
    }
    private sealed class Fixture : IDisposable
    {
        public AppearanceUiService Service { get; }
        public FakeExternalLauncher External { get; } = new();
        public MainWindowViewModel Root { get; }
        public AccountsPageViewModel Vm => Root.Accounts;
        public Window Window { get; }
        public byte[] PngBytes { get; } = Png();
        public string PngPath { get; }
        private Fixture(AppearanceUiService service)
        {
            Service = service; Root = new(service, External); PngPath = Path.Combine(service.Paths.TempDirectory, "fixture-skin.png"); File.WriteAllBytes(PngPath, PngBytes);
            Window = new() { Width = 976, Height = 640, Content = new AccountsView { Width = 300, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, DataContext = Vm } };
        }
        public static async Task<Fixture> OpenAsync(bool seed = true, AppearanceUiService? service = null)
        {
            service ??= new();
            if (seed && service.Snapshot.Accounts[0].Kind == AccountKind.Offline) await service.Store.SaveOfflineAsync(service.Snapshot.Accounts[0], Png(), AccountSkinModel.Classic);
            var fixture = new Fixture(service); await fixture.Vm.ReadAccountAppearanceAsync(false); fixture.Window.Show(); Pump(fixture.Window); return fixture;
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
    private static byte[] Png(int height = 64)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++) { raw.WriteByte(0); for (var x = 0; x < 64; x++) raw.Write(x is >= 8 and < 16 && y is >= 8 and < 16 ? new byte[] { 220, 0, 0, 255 } : x is >= 24 and < 32 && y is >= 8 and < 16 ? new byte[] { 0, 0, 220, 255 } : new byte[] { 80, 100, 70, 255 }); }
        using var png = new MemoryStream(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]); var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 64); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream(); using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw.ToArray()); Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return png.ToArray();
        void Chunk(string tag, byte[] bytes) { var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); png.Write(length); var data = Encoding.ASCII.GetBytes(tag).Concat(bytes).ToArray(); png.Write(data); var crc = uint.MaxValue; foreach (var b in data) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) * 0xedb88320u); } BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); png.Write(length); }
    }
    private sealed class AppearanceUiService : ILauncherService, IAccountAppearanceService
    {
        public FakeLauncherService Base { get; } = new();
        public IAppPaths Paths { get; } = new AppearancePaths();
        public AccountAppearanceStore Store { get; }
        public int SaveCalls;
        public Func<string, bool, CancellationToken, Task<AccountAppearance>>? Read;
        public AppearanceUiService()
        { Store = new(Paths.LocalDataDirectory); Base.Publish(Base.Snapshot with { Accounts = [Base.Snapshot.Accounts[0], new("offline:second", AccountKind.Offline, "第二夹具", new string('b', 32))] }); }
        public Task<AccountAppearance> GetAccountAppearanceAsync(string id, bool remote = false, CancellationToken ct = default) => Read?.Invoke(id, remote, ct) ?? Store.LoadOfflineAsync(Snapshot.Accounts.Single(a => a.Id == id), ct);
        public Task<AccountAppearance> SaveOfflineAccountSkinAsync(string id, ReadOnlyMemory<byte> png, AccountSkinModel model, CancellationToken ct = default) { Assert.Equal(Snapshot.SelectedAccountId, id); SaveCalls++; return Store.SaveOfflineAsync(Snapshot.Accounts.Single(a => a.Id == id), png, model, ct); }
        public LauncherSnapshot Snapshot => Base.Snapshot;
        public IPlatformCapabilities Capabilities => Base.Capabilities;
        public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => Base.SnapshotChanged += value; remove => Base.SnapshotChanged -= value; }
        public event EventHandler<LauncherLog>? LogReceived { add => Base.LogReceived += value; remove => Base.LogReceived -= value; }
        // Forward the unrelated launcher contract to the existing explicit fake.
        public Task InitializeAsync(CancellationToken ct = default) => Base.InitializeAsync(ct);
        public Task<GameInstance> CreateInstanceAsync(string name, string versionId, CancellationToken ct = default) => Base.CreateInstanceAsync(name, versionId, ct);
        public Task<GameInstance> CreateFabricInstanceAsync(string name, string gameVersionId, string loaderVersion, CancellationToken ct = default) => Base.CreateFabricInstanceAsync(name, gameVersionId, loaderVersion, ct);
        public Task SelectInstanceAsync(string id, CancellationToken ct = default) => Base.SelectInstanceAsync(id, ct);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken ct = default) => Base.DiscoverJavaAsync(ct);
        public Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string java, CancellationToken ct = default) => Base.InspectJavaTrustAsync(java, ct);
        public Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string java, CancellationToken ct = default) => Base.CreateRepairedJavaCopyAsync(java, ct);
        public Task InstallInstanceAsync(string id, CancellationToken ct = default) => Base.InstallInstanceAsync(id, ct);
        public Task<LaunchPlan> BuildLaunchPlanAsync(string id, CancellationToken ct = default) => Base.BuildLaunchPlanAsync(id, ct);
        public Task LaunchAsync(string id, string serverId = "none", CancellationToken ct = default) => Base.LaunchAsync(id, serverId, ct);
        public Task StopGameAsync(CancellationToken ct = default) => Base.StopGameAsync(ct);
        public Task CancelDownloadAsync(string id, CancellationToken ct = default) => Base.CancelDownloadAsync(id, ct);
        public Task RetryDownloadAsync(string id, CancellationToken ct = default) => Base.RetryDownloadAsync(id, ct);
        public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default) => Base.SaveSettingsAsync(settings, ct);
        public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken ct = default) => Base.SaveConnectionSecretAsync(kind, secret, ct);
        public Task<AccountProfile> AddOfflineAccountAsync(string name, CancellationToken ct = default) => Base.AddOfflineAccountAsync(name, ct);
        public Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken ct = default) => Base.LoginMicrosoftAsync(challenge, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string name, string password, CancellationToken ct = default) => Base.LoginYggdrasilAsync(server, name, password, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string name, string password, Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> select, CancellationToken ct = default) => Base.LoginYggdrasilWithProfileSelectionAsync(server, name, password, select, ct);
        public Task RefreshAccountAsync(string id, CancellationToken ct = default) => Base.RefreshAccountAsync(id, ct);
        public Task SelectAccountAsync(string id, CancellationToken ct = default) => Base.SelectAccountAsync(id, ct);
        public Task RemoveAccountAsync(string id, CancellationToken ct = default) => Base.RemoveAccountAsync(id, ct);
        public Task RefreshServersAsync(CancellationToken ct = default) => Base.RefreshServersAsync(ct);
        public IReadOnlyList<LauncherLog> GetRecentLogs() => Base.GetRecentLogs();
        public ValueTask DisposeAsync() => Base.DisposeAsync();
    }
    private sealed class AppearancePaths : IAppPaths
    {
        public string DataDirectory { get; } = Path.Combine(Environment.GetEnvironmentVariable("SHOU_DEV_ROOT") ?? throw new InvalidOperationException("Guarded test root required."), "test-data/r5/appearance-ui/" + Guid.NewGuid().ToString("N"));
        public AppearancePaths() { foreach (var directory in new[] { DataDirectory, LocalDataDirectory, TempDirectory }) Directory.CreateDirectory(directory); }
        public string SharedDataDirectory => DataDirectory + "/shared";
        public string LocalDataDirectory => DataDirectory + "/local";
        public string CacheDirectory => DataDirectory + "/cache";
        public string LogsDirectory => DataDirectory + "/logs";
        public string RuntimesDirectory => DataDirectory + "/runtimes";
        public string InstancesDirectory => DataDirectory + "/instances";
        public string TempDirectory => DataDirectory + "/temp";
        public string ResourcesDirectory => DataDirectory + "/resources";
        public string LockFile => DataDirectory + "/instance.lock";
        public string CredentialScope => "appearance-ui-fixture";
        public bool IsIsolated => true;
    }
}
