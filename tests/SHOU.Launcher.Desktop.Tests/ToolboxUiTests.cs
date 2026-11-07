using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Authentication;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ToolboxUiTests
{
    [AvaloniaFact]
    public async Task DownloadBusyIsLocalAndNavigationCancelsWithoutPublishingGlobalProgress()
    {
        var service = new ToolService(); CancellationToken received = default;
        service.DownloadBehavior = async ct => { received = ct; await Task.Delay(Timeout.Infinite, ct); };
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); vm.ShowToolsCommand.Execute(null); vm.Tools.ShowToolboxCommand.Execute(null);
        var tools = vm.Tools.Toolbox; tools.DownloadUrl = "https://example.invalid/fixture.bin";
        Assert.Equal("fixture.bin", tools.DownloadName); Assert.True(tools.DownloadCommand.CanExecute(null));
        var work = tools.DownloadCommand.ExecuteAsync(null);
        Assert.True(tools.IsDownloadBusy); Assert.False(vm.IsBusy); Assert.False(tools.DownloadCommand.CanExecute(null));
        vm.ShowDownloadsCommand.Execute(null); Assert.True(received.IsCancellationRequested); await work;
        Assert.False(tools.IsBusy); Assert.Contains("已取消", tools.DownloadFeedback); Assert.Equal("fixture.bin", tools.DownloadName);
    }
    [AvaloniaFact]
    public async Task ToolCommandsUseServicesAndCleanupRequiresReviewedConfirmation()
    {
        var service = new ToolService();
        service.Publish(service.Snapshot with { Instances = [new("fixture", "Fixture", "1.21.1", "C:/fixture", Presentation: new(LaunchCount: 7))] });
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); var tools = vm.Tools.Toolbox;
        await tools.FortuneCommand.ExecuteAsync(null); Assert.Contains("42 / 100", tools.ActionFeedback);
        Assert.False(tools.HasCleanupControls);
        tools.LaunchCountCommand.Execute(null); Assert.Contains("7 次", tools.ActionFeedback);
        await tools.ScanCleanupCommand.ExecuteAsync(null); Assert.True(tools.HasCleanupPreview); Assert.False(tools.MoveCleanupCommand.CanExecute(null));
        Assert.True(tools.HasCleanupControls);
        tools.ConfirmCleanup = true; Assert.True(tools.MoveCleanupCommand.CanExecute(null));
        await tools.MoveCleanupCommand.ExecuteAsync(null); Assert.True(service.Moved); Assert.False(tools.HasCleanupPreview); Assert.False(tools.ConfirmCleanup);
        tools.ServerAddress = "server.example.invalid:25565"; await tools.QueryServerCommand.ExecuteAsync(null); Assert.Contains("在线 · 12 人", tools.ServerFeedback);
        Assert.False(vm.IsBusy); Assert.Null(vm.ErrorMessage);
    }
    [AvaloniaFact]
    public void CommunityCardComesFirstAndFunctionalFieldsAreEditableWithoutDefaultExtraHints()
    {
        using var vm = new MainWindowViewModel(new ToolService(), new FakeExternalLauncher()); vm.ShowToolsCommand.Execute(null); vm.Tools.ShowToolboxCommand.Execute(null);
        var window = new MainWindow { DataContext = vm }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        try
        {
            var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
            var community = controls.OfType<Border>().Single(c => c.Name == "ToolCommunityCard"); var actions = controls.OfType<Border>().Single(c => c.Name == "ToolboxActionsCard");
            Assert.True(community.Bounds.Y < actions.Bounds.Y);
            foreach (var id in new[] { "CustomDownloadUrl", "ToolDownloadDirectory", "ToolDownloadFileName", "ToolSkinPlayerName", "ToolServerAddress" }) Assert.True(controls.OfType<TextBox>().Single(c => c.Name == id).IsEffectivelyEnabled);
            Assert.False(controls.OfType<TextBlock>().Single(c => c.Name == "ToolActionFeedback").IsEffectivelyVisible);
            Assert.False(controls.OfType<TextBlock>().Single(c => c.Name == "ToolDownloadFeedback").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task OfflineUuidRadiosValidateCustomAndPassTheChosenModeWithoutDroppingChineseName()
    {
        var service = new ToolService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        vm.Accounts.SelectProviderCommand.Execute("offline"); vm.Accounts.PlayerName = "中文角色";
        vm.Accounts.IsCustomUuid = true; vm.Accounts.CustomOfflineUuid = "broken"; Assert.False(vm.Accounts.AddOfflineCommand.CanExecute(null));
        vm.Accounts.CustomOfflineUuid = "12345678-90AB-CDEF-1234-567890ABCDEF"; Assert.True(vm.Accounts.AddOfflineCommand.CanExecute(null));
        await vm.Accounts.AddOfflineCommand.ExecuteAsync(null); Assert.Equal(OfflineUuidMode.Custom, service.LastUuidMode);
        Assert.Equal("1234567890abcdef1234567890abcdef", service.Snapshot.Accounts.Single().PlayerUuid); Assert.Equal("中文角色", service.Snapshot.Accounts.Single().DisplayName);
        Assert.False(vm.Accounts.IsAddingAccount);
    }
    private sealed class ToolService : FakeLauncherService, IToolboxService, IOfflineAccountService
    {
        public Func<CancellationToken, Task>? DownloadBehavior { get; set; }
        public bool Moved { get; private set; }
        public OfflineUuidMode LastUuidMode { get; private set; }
        public Task DownloadToolFileAsync(Uri source, string destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => DownloadBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public Task<AccountTextureImage> DownloadPublicSkinAsync(string playerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClubServer> QueryToolServerAsync(string address, CancellationToken cancellationToken = default) => Task.FromResult(new ClubServer("tool", "Server", address, "在线", 12));
        public Task<GameLogCleanupPlan> ScanGameLogsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameLogCleanupPlan("fixture", [new("fixture", "logs/old.log", 4, new string('a', 64))]));
        public Task MoveGameLogsAsync(GameLogCleanupPlan plan, CancellationToken cancellationToken = default) { Moved = true; return Task.CompletedTask; }
        public Task<int> RestoreGameLogsAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<int> GetDailyFortuneAsync(DateOnly date, CancellationToken cancellationToken = default) => Task.FromResult(42);
        public Task<AccountProfile> AddOfflineAccountAsync(string playerName, OfflineUuidMode mode, string? customUuid = null, CancellationToken cancellationToken = default)
        { LastUuidMode = mode; var uuid = OfflineIdentity.Create(playerName, mode, customUuid); var account = new AccountProfile("offline:" + uuid, AccountKind.Offline, playerName, uuid); Publish(Snapshot with { Accounts = [account], SelectedAccountId = account.Id }); return Task.FromResult(account); }
    }
}
