using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class AuthenticationSelectionTests
{
    private static readonly AuthenticationProfileChoice[] Choices =
    [new("11111111222233334444555555555555", "SameName"), new("aaaaaaaa222233334444555555555555", "SameName")];

    private static MainWindow Open(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show(); Pump(window); vm.SelectedPage = vm.Accounts; Pump(window);
        return window;
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void StartYggdrasil(MainWindowViewModel vm)
    {
        vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "mua");
        vm.Accounts.Username = "fixture-user"; vm.Accounts.Password = "fixture-selection-password";
        vm.Accounts.LoginYggdrasilCommand.Execute(null);
    }
    private static async Task<AuthenticationProfileChallengeViewModel> AwaitChallenge(MainWindow window, MainWindowViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.Accounts.ProfileChallenge is null && DateTime.UtcNow < deadline) { await Task.Delay(5); Pump(window); }
        Pump(window);
        return Assert.IsType<AuthenticationProfileChallengeViewModel>(vm.Accounts.ProfileChallenge);
    }
    private static Task AwaitLogin(MainWindowViewModel vm) => vm.Accounts.LoginYggdrasilCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));

    [AvaloniaFact]
    public async Task UnboundLoginRequiresExplicitSelectionAndAuthorizesOnlyChosenId()
    {
        var fake = new FakeLauncherService { UnboundYggdrasilProfiles = Choices, ProfileSelectionOnWorker = true };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            StartYggdrasil(vm);
            var challenge = await AwaitChallenge(window, vm);
            Assert.True(vm.IsBusy); Assert.Single(fake.Snapshot.Accounts);
            Assert.Null(challenge.SelectedChoice);
            Assert.False(Find<Button>(window, "ConfirmAuthenticationProfileButton").IsEffectivelyEnabled);
            Assert.False(Find<ComboBox>(window, "LoginProviderSelector").IsEnabled);
            var selector = Find<ListBox>(window, "AuthenticationProfileSelector");
            selector.SelectedItem = challenge.Choices[1]; Pump(window);
            var confirm = Find<Button>(window, "ConfirmAuthenticationProfileButton");
            Assert.True(confirm.IsEffectivelyEnabled); confirm.Command!.Execute(null);
            await AwaitLogin(vm); Pump(window);
            Assert.Equal(Choices[1].Id, fake.LastSelectedProfileId);
            Assert.Equal(2, fake.Snapshot.Accounts.Count);
            Assert.Equal(Choices[1].Id, fake.Snapshot.Accounts.Single(a => a.Kind == AccountKind.Yggdrasil).PlayerUuid);
            Assert.Equal(1, fake.ProfileSelectorCalls); Assert.Equal(0, fake.OutstandingProfileSelections);
            Assert.False(vm.Accounts.HasProfileChallenge); Assert.False(vm.IsBusy);
            Assert.Equal("", vm.Accounts.Password);
            Assert.DoesNotContain("fixture-selection-password", vm.Logs.ExportText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OldChallengeCommandsCannotSelectOrCancelNewAuthentication()
    {
        var fake = new FakeLauncherService { UnboundYggdrasilProfiles = Choices };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            StartYggdrasil(vm);
            var old = await AwaitChallenge(window, vm);
            old.SelectedChoice = old.Choices[0];
            old.CancelCommand.Execute(null); await AwaitLogin(vm); Pump(window);
            Assert.False(old.IsPending); Assert.False(old.ConfirmCommand.CanExecute(null));
            StartYggdrasil(vm);
            var current = await AwaitChallenge(window, vm);
            Assert.NotSame(old, current);
            old.ConfirmCommand.Execute(null); old.CancelCommand.Execute(null); Pump(window);
            Assert.True(current.IsPending); Assert.True(vm.IsBusy); Assert.Null(fake.LastSelectedProfileId);
            current.CancelCommand.Execute(null); await AwaitLogin(vm); Pump(window);
            Assert.Equal(0, fake.OutstandingProfileSelections); Assert.Single(fake.Snapshot.Accounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingAccountPageCancelsPendingProfileSelection()
    {
        var fake = new FakeLauncherService { UnboundYggdrasilProfiles = Choices };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            StartYggdrasil(vm);
            var challenge = await AwaitChallenge(window, vm);
            vm.SelectedPage = vm.Home;
            await AwaitLogin(vm); Pump(window);
            Assert.False(challenge.IsPending); Assert.False(vm.Accounts.IsAuthenticating);
            Assert.Null(vm.Accounts.ProfileChallenge); Assert.Equal(0, fake.OutstandingProfileSelections);
            Assert.False(vm.IsBusy); Assert.Single(fake.Snapshot.Accounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWindowFinishesProfileWaitAndLeavesHostServiceOwnershipIntact()
    {
        var fake = new FakeLauncherService { UnboundYggdrasilProfiles = Choices, ProfileSelectionOnWorker = true };
        var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        StartYggdrasil(vm);
        var challenge = await AwaitChallenge(window, vm);
        window.Close();
        await AwaitLogin(vm);
        Assert.False(challenge.IsPending); Assert.Equal(0, fake.OutstandingProfileSelections);
        Assert.Equal(0, fake.DisposeCalls); Assert.Equal(0, fake.SnapshotSubscribers); Assert.Equal(0, fake.LogSubscribers);
    }

    [AvaloniaFact]
    public async Task LeavingPageCancelsDevicePollingAndRejectsLateChallenge()
    {
        IProgress<DeviceCodeChallenge>? reporter = null;
        var fake = new FakeLauncherService { MicrosoftBehavior = async (progress, ct) =>
        {
            reporter = progress;
            progress.Report(new("FIRST-FIXTURE", new Uri("https://microsoft.com/link"), DateTimeOffset.UtcNow.AddMinutes(5), "fixture"));
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        } };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "microsoft");
            vm.Accounts.LoginMicrosoftCommand.Execute(null); Pump(window);
            Assert.True(vm.Accounts.HasDeviceChallenge);
            vm.SelectedPage = vm.Settings;
            await vm.Accounts.LoginMicrosoftCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            reporter!.Report(new("LATE-FIXTURE", new Uri("https://microsoft.com/link"), DateTimeOffset.UtcNow.AddMinutes(5), "late fixture"));
            vm.SelectedPage = vm.Accounts; Pump(window);
            Assert.False(vm.Accounts.HasDeviceChallenge); Assert.Equal("", vm.Accounts.DeviceCode);
            Assert.False(vm.IsBusy); Assert.Single(fake.Snapshot.Accounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GlobalCancelCompletesProfileWaitWithoutSelectingDefaultRole()
    {
        var fake = new FakeLauncherService { UnboundYggdrasilProfiles = Choices };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            StartYggdrasil(vm);
            var challenge = await AwaitChallenge(window, vm);
            window.FindControl<Button>("CancelOperationButton")!.Command!.Execute(null);
            await AwaitLogin(vm); Pump(window);
            Assert.False(challenge.IsPending); Assert.Null(fake.LastSelectedProfileId);
            Assert.Equal(0, fake.OutstandingProfileSelections); Assert.Single(fake.Snapshot.Accounts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AlreadyBoundLoginDoesNotInventRoleSelection()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        var window = Open(vm);
        try
        {
            StartYggdrasil(vm); await AwaitLogin(vm); Pump(window);
            Assert.Equal(0, fake.ProfileSelectorCalls); Assert.False(vm.Accounts.HasProfileChallenge);
            Assert.Single(fake.Snapshot.Accounts, a => a.Kind == AccountKind.Yggdrasil);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MemoryLimitAndMalformedProxyCredentialsAreRejectedBeforeSaving()
    {
        var fake = new FakeLauncherService();
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        vm.Settings.MemoryMiB = 32769;
        await vm.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, fake.SaveCalls); Assert.Contains("32768", vm.ErrorMessage);
        vm.Instances.MemoryMiB = 32769;
        await vm.Instances.SaveRuntimeCommand.ExecuteAsync(null);
        Assert.Equal(0, fake.SaveCalls); Assert.Contains("32768", vm.ErrorMessage);
        vm.Settings.MemoryMiB = 2048; vm.Settings.ProxySecret = "fixture-missing-user-separator";
        await vm.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, fake.SaveCalls); Assert.Empty(fake.SavedSecretKinds);
        Assert.Contains("username:password", vm.ErrorMessage); Assert.DoesNotContain("fixture-missing-user-separator", vm.ErrorMessage);
    }
}
