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
    public async Task LateTransferCallbacksCannotReplaceSuccessOrCancellationFeedback()
    {
        var service = new ToolService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        var tools = vm.Tools.Toolbox; tools.DownloadUrl = "https://example.invalid/fixture.bin";
        await tools.DownloadCommand.ExecuteAsync(null); var success = tools.DownloadFeedback;
        service.LastProgress!.Report(new("fixture", "Downloading", 42, 100)); Dispatcher.UIThread.RunJobs(); Assert.Equal(success, tools.DownloadFeedback);
        service.DownloadBehavior = ct => Task.Delay(Timeout.Infinite, ct);
        var work = tools.DownloadCommand.ExecuteAsync(null); var prior = service.LastProgress!; tools.CancelCommand.Execute(null); await work;
        var cancelled = tools.DownloadFeedback; prior.Report(new("fixture", "Downloading", 99, 100)); Dispatcher.UIThread.RunJobs(); Assert.Equal(cancelled, tools.DownloadFeedback);
    }
    [AvaloniaFact]
    public async Task ClosingWaitsForCancelledToolCleanupBeforeDestroyingTheWindow()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken received = default;
        var service = new ToolService { DownloadBehavior = async ct => { received = ct; try { await Task.Delay(Timeout.Infinite, ct); } finally { await cleanup.Task; } } };
        using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); var window = new MainWindow { DataContext = vm }; window.Show();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            vm.Tools.Toolbox.DownloadUrl = "https://example.invalid/fixture.bin"; var work = vm.Tools.Toolbox.DownloadCommand.ExecuteAsync(null);
            window.Close(); Assert.True(received.IsCancellationRequested); Assert.True(window.IsVisible); Assert.False(closed.Task.IsCompleted);
            cleanup.SetResult(); await work;
            // Tool completion precedes settings flush and the final Close continuation.
            // Observe the actual lifecycle event rather than assume two dispatcher turns suffice.
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
        }
        finally { cleanup.TrySetResult(); window.Close(); }
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
    [AvaloniaFact]
    public async Task CachePreviewAndConfirmationDispatchLocallyAndNeverShowTheGlobalOperationBar()
    {
        var service = new ToolService(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher()); var tools = vm.Tools.Toolbox;
        Assert.False(tools.ClearCacheCommand.CanExecute(null)); await tools.ScanCacheCommand.ExecuteAsync(null);
        Assert.True(tools.HasCachePreview); Assert.False(tools.ClearCacheCommand.CanExecute(null)); Assert.False(vm.IsBusy);
        tools.ConfirmCache = true; Assert.True(tools.ClearCacheCommand.CanExecute(null)); await tools.ClearCacheCommand.ExecuteAsync(null);
        Assert.True(service.CacheCleared); Assert.False(tools.HasCachePreview); Assert.Contains("已清理 1 个文件", tools.CacheFeedback); Assert.False(vm.ShowOperationStatus);
    }
    private sealed class ToolService : FakeLauncherService, IToolboxService, IOfflineAccountService, ICacheMaintenanceService
    {
        public bool CacheCleared;
        public Task<CacheCleanupPlan> ScanLauncherCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CacheCleanupPlan([new("cache", "catalog/fixture.json", 42, new string('a', 64))], 0));
        public Task<CacheCleanupResult> ClearLauncherCacheAsync(CacheCleanupPlan plan, CancellationToken cancellationToken = default) { CacheCleared = true; return Task.FromResult(new CacheCleanupResult(1, 42, 0, false)); }
        public Func<CancellationToken, Task>? DownloadBehavior { get; set; }
        public IProgress<TransferProgress>? LastProgress { get; private set; }
        public bool Moved { get; private set; }
        public OfflineUuidMode LastUuidMode { get; private set; }
        public Task DownloadToolFileAsync(Uri source, string destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) { LastProgress = progress; return DownloadBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask; }
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
