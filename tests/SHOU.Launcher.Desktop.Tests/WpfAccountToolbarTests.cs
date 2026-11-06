using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class WpfAccountToolbarTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static Point Center(Control control, Window window) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public async Task PointerRevealsEqualIconActionsWithoutChangingCardHeightOrLeavingHiddenHitTargets()
    {
        using var fixture = await Fixture.OpenAsync();
        var host = fixture.Toolbar; var panel = Find<StackPanel>(fixture.Window, "SelectedAccountPanel");
        var height = panel.Bounds.Height;
        Assert.Equal(0, host.Opacity); Assert.False(host.IsHitTestVisible); Assert.True(host.IsVisible);
        fixture.Window.MouseMove(Center(Find<Button>(fixture.Window, "AccountAvatarPreviewButton"), fixture.Window)); Pump(fixture.Window);
        Assert.Equal(1, host.Opacity); Assert.True(host.IsHitTestVisible); Assert.Equal(height, panel.Bounds.Height);
        var skin = Find<Button>(fixture.Window, "AccountSkinMenuButton"); var refresh = Find<Button>(fixture.Window, "RefreshAccountButton");
        Assert.Equal(skin.Bounds.Size, refresh.Bounds.Size);
        Assert.IsType<ClubIcon>(skin.Content); Assert.IsType<ClubIcon>(refresh.Content);
        Assert.Equal("皮肤与披风", Avalonia.Automation.AutomationProperties.GetName(skin));
        Assert.Equal("账户操作", Avalonia.Automation.AutomationProperties.GetName(refresh));
        var accountMenu = Assert.IsType<MenuFlyout>(refresh.Flyout);
        accountMenu.ShowAt(refresh); Pump(fixture.Window);
        Assert.Same(fixture.Vm.RefreshAccountCommand, accountMenu.Items.OfType<MenuItem>().First().Command);
        accountMenu.Hide();
        var select = Find<Button>(fixture.Window, "SelectAccountProfileButton");
        Assert.Equal(skin.Bounds.Size, select.Bounds.Size);
        Assert.Equal("选择档案", Avalonia.Automation.AutomationProperties.GetName(select));
        Assert.Same(fixture.Vm.ToggleAllAccountsCommand, select.Command);
        fixture.MoveOutside();
        Assert.Equal(0, host.Opacity); Assert.False(host.IsHitTestVisible); Assert.Equal(height, panel.Bounds.Height);
        fixture.Window.MouseMove(Center(host, fixture.Window)); Pump(fixture.Window);
        Assert.Equal(1, host.Opacity); Assert.True(host.IsHitTestVisible); Assert.Equal(height, panel.Bounds.Height);
    }

    [AvaloniaFact]
    public async Task RealTabNavigationCanReachAndRevealTheHiddenActions()
    {
        using var fixture = await Fixture.OpenAsync();
        Assert.Equal(0, fixture.Toolbar.Opacity);
        var skin = Find<Button>(fixture.Window, "AccountSkinMenuButton");
        for (var key = 0; key < 24 && !skin.IsFocused; key++)
        { fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Pump(fixture.Window); }
        Assert.True(skin.IsFocused); Assert.Equal(1, fixture.Toolbar.Opacity); Assert.True(fixture.Toolbar.IsHitTestVisible);
        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Pump(fixture.Window);
        Assert.True(Find<Button>(fixture.Window, "RefreshAccountButton").IsFocused);
        fixture.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Pump(fixture.Window);
        Assert.True(Find<Button>(fixture.Window, "SelectAccountProfileButton").IsFocused);
        fixture.Outside.Focus(); Pump(fixture.Window);
        Assert.Equal(0, fixture.Toolbar.Opacity); Assert.False(fixture.Toolbar.IsHitTestVisible);
    }

    [AvaloniaFact]
    public async Task ActualMenuClickKeepsActionsVisibleOutsideTheCardAndDetachmentClosesTheFlyout()
    {
        using var fixture = await Fixture.OpenAsync();
        var skin = Find<Button>(fixture.Window, "AccountSkinMenuButton"); var host = fixture.Toolbar;
        var center = Center(skin, fixture.Window);
        fixture.Window.MouseMove(center); Pump(fixture.Window);
        fixture.Window.MouseDown(center, MouseButton.Left); fixture.Window.MouseUp(center, MouseButton.Left); Pump(fixture.Window);
        var flyout = Assert.IsType<MenuFlyout>(skin.Flyout);
        Assert.True(flyout.IsOpen);
        Assert.Equal(new[] { "ChangeAccountSkinMenuItem", "SaveAccountSkinMenuItem", "RefreshAccountSkinMenuItem", "ChangeAccountCapeMenuItem", "PreviewAccountSkinMenuItem" }, flyout.Items.OfType<MenuItem>().Where(item => item.Name is not null).Select(item => item.Name));
        fixture.MoveOutside(); Assert.True(flyout.IsOpen); Assert.Equal(1, host.Opacity);
        fixture.Window.Content = new TextBlock { Text = "Detached account view" }; Pump(fixture.Window);
        Assert.False(flyout.IsOpen); Assert.Equal(0, host.Opacity); Assert.False(host.IsHitTestVisible);
        fixture.Window.Content = fixture.Content; fixture.Outside.Focus(); fixture.MoveOutside();
        Assert.Equal(0, fixture.Toolbar.Opacity);
        fixture.Window.MouseMove(Center(Find<Button>(fixture.Window, "AccountAvatarPreviewButton"), fixture.Window)); Pump(fixture.Window);
        Assert.Equal(1, fixture.Toolbar.Opacity);
    }

    [AvaloniaFact]
    public async Task AvatarTooltipKeepsStaticDetailsWhileActualLoadingAndErrorsRemainVisible()
    {
        using var fixture = await Fixture.OpenAsync();
        var feedback = Find<TextBlock>(fixture.Window, "AccountAppearanceFeedback");
        Assert.False(feedback.IsVisible);
        var avatar = Find<Button>(fixture.Window, "AccountAvatarPreviewButton");
        ToolTip.SetIsOpen(avatar, true); Pump(fixture.Window);
        try
        {
            var tooltip = Assert.IsType<StackPanel>(ToolTip.GetTip(avatar));
            var text = tooltip.Children.OfType<TextBlock>().ToArray();
            Assert.Equal(3, text.Length); Assert.Equal(fixture.Vm.SelectedPlayerStatus, text[0].Text);
            Assert.Equal(fixture.Vm.AccountAppearanceSummary, text[1].Text); Assert.Equal(fixture.Vm.AccountAppearanceCapability, text[2].Text);
        }
        finally { ToolTip.SetIsOpen(avatar, false); }
        var pending = new TaskCompletionSource<AccountAppearance>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Read = (_, _, _) => pending.Task;
        var read = fixture.Vm.ReadAccountAppearanceAsync(true);
        try { Pump(fixture.Window); Assert.True(feedback.IsVisible); Assert.Equal("正在读取外观…", feedback.Text); }
        finally { pending.TrySetException(new IOException("fixture appearance failure")); await read.WaitAsync(Deadline); }
        Pump(fixture.Window); Assert.True(feedback.IsVisible); Assert.Equal("fixture appearance failure", feedback.Text);
    }

    private sealed class Fixture : IDisposable
    {
        public ToolbarService Service { get; } = new();
        public MainWindowViewModel Root { get; }
        public AccountsPageViewModel Vm => Root.Accounts;
        public Window Window { get; }
        public Grid Content { get; } = new() { ColumnDefinitions = new ColumnDefinitions("300,*") };
        public Button Outside { get; } = new() { Content = "其他内容", Width = 120, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        public Border Toolbar => Find<Border>(Window, "AccountToolbarHost");
        private Fixture()
        {
            Root = new(Service, new FakeExternalLauncher());
            Content.Children.Add(new AccountsView { DataContext = Vm }); Grid.SetColumn(Outside, 1); Content.Children.Add(Outside);
            Window = new() { Width = 520, Height = 560, Content = Content };
        }
        public static async Task<Fixture> OpenAsync()
        {
            var fixture = new Fixture();
            try { await fixture.Vm.ReadAccountAppearanceAsync(false); fixture.Window.Show(); Pump(fixture.Window); fixture.Outside.Focus(); fixture.MoveOutside(); return fixture; }
            catch { fixture.Dispose(); throw; }
        }
        public void MoveOutside() { Window.MouseMove(Center(Outside, Window)); Pump(Window); }
        public void Dispose() { Window.Close(); Root.Dispose(); }
    }

    private sealed class ToolbarService : ILauncherService, IAccountAppearanceService
    {
        private readonly FakeLauncherService service = new();
        public Func<string, bool, CancellationToken, Task<AccountAppearance>>? Read;
        public ToolbarService()
        {
            var profile = new AccountProfile("microsoft:toolbar-fixture", AccountKind.Microsoft, "工具条夹具", new string('a', 32), Persisted: true);
            service.Publish(service.Snapshot with { Accounts = [profile], SelectedAccountId = profile.Id });
        }
        public Task<AccountAppearance> GetAccountAppearanceAsync(string id, bool remote = false, CancellationToken ct = default) => Read?.Invoke(id, remote, ct) ?? Task.FromResult(AccountAppearanceClient.Unread(Snapshot.Accounts.Single(account => account.Id == id)));
        public Task<AccountAppearance> SaveOfflineAccountSkinAsync(string id, ReadOnlyMemory<byte> png, AccountSkinModel model, CancellationToken ct = default) => throw new NotSupportedException();
        public IAppPaths Paths => service.Paths;
        public LauncherSnapshot Snapshot => service.Snapshot;
        public IPlatformCapabilities Capabilities => service.Capabilities;
        public event EventHandler<LauncherSnapshot>? SnapshotChanged { add => service.SnapshotChanged += value; remove => service.SnapshotChanged -= value; }
        public event EventHandler<LauncherLog>? LogReceived { add => service.LogReceived += value; remove => service.LogReceived -= value; }
        public Task InitializeAsync(CancellationToken ct = default) => service.InitializeAsync(ct);
        public Task<GameInstance> CreateInstanceAsync(string name, string version, CancellationToken ct = default) => service.CreateInstanceAsync(name, version, ct);
        public Task<GameInstance> CreateFabricInstanceAsync(string name, string game, string loader, CancellationToken ct = default) => service.CreateFabricInstanceAsync(name, game, loader, ct);
        public Task SelectInstanceAsync(string id, CancellationToken ct = default) => service.SelectInstanceAsync(id, ct);
        public Task<IReadOnlyList<JavaInstallation>> DiscoverJavaAsync(CancellationToken ct = default) => service.DiscoverJavaAsync(ct);
        public Task<IReadOnlyList<JavaTrustFinding>> InspectJavaTrustAsync(string java, CancellationToken ct = default) => service.InspectJavaTrustAsync(java, ct);
        public Task<JavaTrustDerivation> CreateRepairedJavaCopyAsync(string java, CancellationToken ct = default) => service.CreateRepairedJavaCopyAsync(java, ct);
        public Task InstallInstanceAsync(string id, CancellationToken ct = default) => service.InstallInstanceAsync(id, ct);
        public Task<LaunchPlan> BuildLaunchPlanAsync(string id, CancellationToken ct = default) => service.BuildLaunchPlanAsync(id, ct);
        public Task LaunchAsync(string id, string serverId = "none", CancellationToken ct = default) => service.LaunchAsync(id, serverId, ct);
        public Task StopGameAsync(CancellationToken ct = default) => service.StopGameAsync(ct);
        public Task CancelDownloadAsync(string id, CancellationToken ct = default) => service.CancelDownloadAsync(id, ct);
        public Task RetryDownloadAsync(string id, CancellationToken ct = default) => service.RetryDownloadAsync(id, ct);
        public Task SaveSettingsAsync(LauncherSettings settings, CancellationToken ct = default) => service.SaveSettingsAsync(settings, ct);
        public Task SaveConnectionSecretAsync(string kind, string secret, CancellationToken ct = default) => service.SaveConnectionSecretAsync(kind, secret, ct);
        public Task<AccountProfile> AddOfflineAccountAsync(string name, CancellationToken ct = default) => service.AddOfflineAccountAsync(name, ct);
        public Task<AccountProfile> LoginMicrosoftAsync(IProgress<DeviceCodeChallenge> challenge, CancellationToken ct = default) => service.LoginMicrosoftAsync(challenge, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilAsync(Uri server, string name, string password, CancellationToken ct = default) => service.LoginYggdrasilAsync(server, name, password, ct);
        public Task<IReadOnlyList<AccountProfile>> LoginYggdrasilWithProfileSelectionAsync(Uri server, string name, string password, Func<IReadOnlyList<AuthenticationProfileChoice>, CancellationToken, Task<string?>> select, CancellationToken ct = default) => service.LoginYggdrasilWithProfileSelectionAsync(server, name, password, select, ct);
        public Task RefreshAccountAsync(string id, CancellationToken ct = default) => service.RefreshAccountAsync(id, ct);
        public Task SelectAccountAsync(string id, CancellationToken ct = default) => service.SelectAccountAsync(id, ct);
        public Task RemoveAccountAsync(string id, CancellationToken ct = default) => service.RemoveAccountAsync(id, ct);
        public Task RefreshServersAsync(CancellationToken ct = default) => service.RefreshServersAsync(ct);
        public IReadOnlyList<LauncherLog> GetRecentLogs() => service.GetRecentLogs();
        public ValueTask DisposeAsync() => service.DisposeAsync();
    }
}
