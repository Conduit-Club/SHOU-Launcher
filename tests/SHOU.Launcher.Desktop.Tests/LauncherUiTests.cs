using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Logging;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LauncherUiTests
{
    private static MainWindow Open(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Pump(window);
        return window;
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void SixPagesLoadRealControlTreesAndNavigateThroughBoundList()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var previousSink = Logger.Sink;
        var bindingSink = new BindingFailureSink();
        Logger.Sink = bindingSink;
        var window = Open(vm);
        try
        {
            Assert.Equal("潮涌核心社启动器", window.Title);
            Assert.Equal("none", vm.Home.SelectedServerId);
            Assert.Equal("潮涌核心社启动器", Find<TextBlock>(window, "BrandTitle").Text);
            var navigation = window.FindControl<ListBox>("PageNavigation")!;
            var expected = new[] { typeof(HomeView), typeof(InstancesView), typeof(DownloadsView), typeof(AccountsView), typeof(SettingsView), typeof(LogsView) };
            for (var i = 0; i < expected.Length; i++)
            {
                navigation.SelectedItem = vm.Pages[i]; Pump(window);
                Assert.Same(vm.Pages[i], vm.SelectedPage);
                Assert.Contains(window.GetVisualDescendants(), c => c.GetType() == expected[i]);
            }
            Assert.Empty(bindingSink.Errors);
        }
        finally { window.Close(); Logger.Sink = previousSink; }
    }

    [AvaloniaFact]
    public async Task BoundInstallLocksAccountChangesAndCancellationReachesService()
    {
        var fake = new FakeLauncherService { InstallBehavior = ct => Task.Delay(Timeout.Infinite, ct) };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.Accounts.PlayerName = "BusyFixture";
            Assert.True(vm.Accounts.AddOfflineCommand.CanExecute(null));
            vm.SelectedPage = vm.Instances; Pump(window);
            var install = Find<Button>(window, "InstallInstanceButton");
            Assert.True(install.IsEnabled);
            install.Command!.Execute(null); Pump(window);
            Assert.True(vm.IsBusy); Assert.False(vm.CanMutate);
            vm.SelectedPage = vm.Accounts; Pump(window);
            Assert.False(Find<ComboBox>(window, "AccountSelector").IsEnabled);
            // Command CanExecute participates in IsEffectivelyEnabled, not the local IsEnabled value.
            Assert.False(vm.Accounts.AddOfflineCommand.CanExecute(null));
            Assert.False(Find<Button>(window, "AddOfflineButton").IsEffectivelyEnabled);
            var cancel = window.FindControl<Button>("CancelOperationButton")!;
            Assert.True(cancel.IsEnabled); cancel.Command!.Execute(null);
            await vm.Instances.InstallCommand.ExecutionTask!; Pump(window);
            Assert.False(vm.IsBusy); Assert.True(vm.CanMutate);
            Assert.Contains("取消", vm.StateSummary);
            Assert.Equal(0, fake.StopCalls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InstallationFailureIsVisibleRedactedAndRetryable()
    {
        var fake = new FakeLauncherService { InstallBehavior = _ => throw new InvalidOperationException("下载失败 access_token=fixture-sensitive-token") };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            await vm.Instances.InstallCommand.ExecuteAsync(null); Pump(window);
            Assert.True(window.FindControl<Border>("GlobalError")!.IsVisible);
            Assert.Contains("下载失败", window.FindControl<TextBlock>("GlobalErrorText")!.Text);
            Assert.DoesNotContain("fixture-sensitive-token", vm.ErrorMessage);
            Assert.True(vm.Instances.InstallCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DownloadCancelWorksDuringInstallAndFailedTaskCanRetry()
    {
        var fake = new FakeLauncherService();
        fake.Publish(fake.Snapshot with { State = "Installing", Downloads = [new("download-1", "客户端", "Downloading", 25, 100, "https://example.invalid/client"), new("download-2", "资源", "Failed", Error: "hash 校验失败")] });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        Assert.True(vm.Downloads.Tasks[0].CancelCommand.CanExecute(null));
        Assert.False(vm.Downloads.Tasks[1].RetryCommand.CanExecute(null));
        await vm.Downloads.Tasks[0].CancelCommand.ExecuteAsync(null);
        Assert.Equal("download-1", fake.LastCancelledDownloadId);
        fake.Publish(fake.Snapshot with { State = "Ready" });
        await vm.Downloads.Tasks.Single(t => t.Task.Id == "download-2").RetryCommand.ExecuteAsync(null);
        Assert.Equal("download-2", fake.LastRetriedDownloadId);
        Assert.Equal("Queued", fake.Snapshot.Downloads.Single(t => t.Id == "download-2").State);
    }

    [AvaloniaFact]
    public void SettingsDraftSurvivesNavigationAndBackgroundSnapshots()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.SelectedPage = vm.Settings; Pump(window);
            Find<TextBox>(window, "GameRootSetting").Text = "/任务/含空格 游戏"; Pump(window);
            Assert.True(vm.Settings.IsDirty);
            vm.SelectedPage = vm.Home; Pump(window);
            fake.Publish(fake.Snapshot with { Downloads = [new("new", "后台任务", "Downloading")] });
            vm.SelectedPage = vm.Settings; Pump(window);
            Assert.Equal("/任务/含空格 游戏", Find<TextBox>(window, "GameRootSetting").Text);
            Assert.True(Find<Button>(window, "SaveSettingsButton").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SettingsSecretsUseCredentialServiceAndPreserveReturnedReferences()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Settings.Aria2Enabled = true; vm.Settings.Aria2Secret = "fixture-rpc-secret";
        vm.Settings.ProxyMode = ProxyMode.Custom; vm.Settings.ProxyAddress = "http://localhost:7890"; vm.Settings.ProxySecret = "fixture-user:fixture-proxy-secret";
        await vm.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "aria2", "proxy" }, fake.SavedSecretKinds);
        Assert.Equal("aria2-fixed-key", fake.Snapshot.Settings.Aria2!.CredentialKey);
        Assert.Equal("proxy-fixed-key", fake.Snapshot.Settings.Proxy!.CredentialKey);
        Assert.Equal("", vm.Settings.Aria2Secret); Assert.Equal("", vm.Settings.ProxySecret);
        Assert.False(vm.Settings.IsDirty); Assert.Equal(1, fake.SaveCalls);
    }

    [AvaloniaFact]
    public async Task CredentialFailureDoesNotPretendSettingsSavedOrExposeSecret()
    {
        var fake = new FakeLauncherService { FailCredentialSave = true };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Settings.Aria2Enabled = true; vm.Settings.Aria2Secret = "fixture-not-in-error";
        await vm.Settings.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Settings.IsDirty); Assert.Equal(0, fake.SaveCalls);
        Assert.Contains("无法持久保存", vm.ErrorMessage);
        Assert.DoesNotContain("fixture-not-in-error", vm.ErrorMessage);
        Assert.Equal("", vm.Settings.Aria2Secret);
    }

    [AvaloniaFact]
    public async Task MicrosoftChallengeBindsAndCancelDoesNotCreateAccount()
    {
        var fake = new FakeLauncherService { MicrosoftBehavior = async (progress, ct) =>
        {
            progress.Report(new("FIXTURE-CODE", new Uri("https://microsoft.com/link"), DateTimeOffset.UtcNow.AddMinutes(5), "仅测试授权状态"));
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        } };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.SelectedPage = vm.Accounts; vm.Accounts.SelectedProvider = vm.Accounts.Providers[1]; Pump(window);
            Find<Button>(window, "LoginMicrosoftButton").Command!.Execute(null); Pump(window);
            Assert.True(vm.Accounts.HasDeviceChallenge);
            Assert.Equal("FIXTURE-CODE", Find<TextBlock>(window, "DeviceCodeText").Text);
            Assert.False(Find<ComboBox>(window, "LoginProviderSelector").IsEnabled);
            vm.CancelOperationCommand.Execute(null);
            await vm.Accounts.LoginMicrosoftCommand.ExecutionTask!; Pump(window);
            Assert.False(vm.Accounts.HasDeviceChallenge); Assert.Single(fake.Snapshot.Accounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task YggdrasilUsesSelectedProviderAndClearsPassword()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "mua");
        vm.Accounts.Username = "fixture-user"; vm.Accounts.Password = "fixture-only-password";
        await vm.Accounts.LoginYggdrasilCommand.ExecuteAsync(null);
        Assert.Equal("https://skin.mualliance.ltd/api/yggdrasil", fake.LastYggdrasilServer!.AbsoluteUri);
        Assert.Equal("fixture-only-password", fake.LastYggdrasilPassword);
        Assert.Equal("", vm.Accounts.Password);
        Assert.DoesNotContain("fixture-only-password", vm.Logs.ExportText);
    }

    [AvaloniaFact]
    public void DuplicateNamesSelectByStableIdAndRemovalRequiresConfirmation()
    {
        var fake = new FakeLauncherService();
        fake.Publish(fake.Snapshot with { Accounts = [.. fake.Snapshot.Accounts, new("same-name-other-source", AccountKind.Yggdrasil, "同名角色", "00000000000000000000000000000004", "https://littleskin.cn/api/yggdrasil")] });
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Accounts.SelectedAccount = vm.Accounts.Accounts.Single(a => a.Id == "same-name-other-source");
        Assert.Equal("same-name-other-source", fake.LastSelectedAccountId);
        Assert.False(vm.Accounts.RemoveAccountCommand.CanExecute(null));
        vm.Accounts.ConfirmRemoval = true;
        Assert.True(vm.Accounts.RemoveAccountCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task BackgroundEventsReachUiAndClosingUnsubscribesWithoutDisposingHost()
    {
        var fake = new FakeLauncherService();
        var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        await Task.Run(() => { fake.EmitLog("Authorization: Bearer fixture-token"); fake.Publish(fake.Snapshot with { State = "Running", GameProcessId = 99 }); });
        Pump(window);
        Assert.Single(vm.Logs.Entries); Assert.DoesNotContain("fixture-token", vm.Logs.ExportText);
        Assert.True(vm.StopGameCommand.CanExecute(null));
        window.Close();
        Assert.Equal(0, fake.SnapshotSubscribers); Assert.Equal(0, fake.LogSubscribers); Assert.Equal(0, fake.DisposeCalls);
    }

    [AvaloniaFact]
    public async Task DefaultLaunchDoesNotConnectToServerAndStopIsSeparate()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        await vm.Instances.LaunchCommand.ExecuteAsync(null);
        Assert.Equal("none", fake.LastLaunchServerId);
        Assert.False(vm.CancelOperationCommand.CanExecute(null));
        await vm.StopGameCommand.ExecuteAsync(null);
        Assert.Equal(1, fake.StopCalls); Assert.False(vm.HasRunningGame);
    }

    [AvaloniaFact]
    public async Task PlanDiagnosticsNeverSerializeCredentialArguments()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        await vm.Instances.InspectPlanCommand.ExecuteAsync(null);
        Assert.Contains("fixture plan", vm.Instances.PlanDiagnostics);
        Assert.DoesNotContain("never-report-raw-plan", vm.Instances.PlanDiagnostics);
    }

    [AvaloniaFact]
    public void LogsAreBoundedAndRedactExportContent()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        for (var i = 0; i < LogsPageViewModel.Capacity + 7; i++) fake.EmitLog($"line {i} password=fixture-password refresh_token=fixture-refresh");
        Assert.Equal(LogsPageViewModel.Capacity, vm.Logs.Entries.Count);
        Assert.DoesNotContain("fixture-password", vm.Logs.ExportText); Assert.DoesNotContain("fixture-refresh", vm.Logs.ExportText);
    }

    [AvaloniaFact]
    public void MissingBootstrapServicesShowsExplicitFailureWindow()
    {
        var window = App.CreateMainWindow(null, null);
        Assert.Equal("BootstrapFailureWindow", window.Name);
        Assert.Contains("ILauncherService", ((StackPanel)window.Content!).Children.OfType<TextBlock>().Single(t => t.Name == "HostErrorMessage").Text);
    }

    [AvaloniaFact]
    public void UnrelatedProgressPreservesServerSelectionAndOfflineSafetyGate()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            Find<ListBox>(window, "ServerSelector").SelectedItem = vm.Home.Servers.Single(s => s.Id == "smp"); Pump(window);
            Assert.False(vm.Instances.LaunchCommand.CanExecute(null));
            fake.Publish(fake.Snapshot with { Downloads = [new("progress", "后台资源", "Downloading", 1, 10)] }); Pump(window);
            Assert.Equal("smp", vm.Home.SelectedServerId);
            Assert.Equal("smp", ((ServerChoice)Find<ListBox>(window, "ServerSelector").SelectedItem!).Id);
            Assert.False(vm.Instances.LaunchCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CopyButtonUsesRedactedLogBuffer()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            fake.EmitLog("download https://example.invalid/file?api_key=fixture-api-secret");
            vm.SelectedPage = vm.Logs; Pump(window);
            Find<Button>(window, "CopyLogsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield(); Pump(window);
            var clipboard = await window.Clipboard!.TryGetTextAsync();
            Assert.Contains("download", clipboard); Assert.DoesNotContain("fixture-api-secret", clipboard);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidSourceDraftDoesNotOverwriteSavedSettings()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Settings.DownloadSourcesJson = "{invalid";
        await vm.Settings.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Settings.IsDirty); Assert.Equal(0, fake.SaveCalls);
        Assert.Contains("JSON", vm.ErrorMessage);
    }

    private sealed class BindingFailureSink : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) { if (IsEnabled(level, area)) Errors.Add(messageTemplate); }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Errors.Add(messageTemplate + " " + string.Join(" | ", propertyValues.Select(p => p?.ToString())));
        }
    }
}
