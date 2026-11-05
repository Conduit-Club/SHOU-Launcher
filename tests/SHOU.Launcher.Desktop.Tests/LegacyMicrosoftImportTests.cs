using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LegacyMicrosoftImportTests
{
    [AvaloniaFact]
    public async Task ReadRequiresExplicitAccountSelectionAndConfirmation()
    {
        var fake = new FakeLauncherService { SupportsLegacyMicrosoftImport = true,
            LegacyChoices = [new("first", "FixtureOne"), new("second", "FixtureTwo")] };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm }; window.Show();
        try
        {
            vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "microsoft");
            vm.Accounts.BeginAddAccountCommand.Execute(null);
            await vm.Accounts.ReadLegacyMicrosoftAccountsCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal(2, vm.Accounts.LegacyMicrosoftAccounts.Count);
            Assert.Null(vm.Accounts.SelectedLegacyMicrosoftAccount);
            Assert.False(vm.Accounts.ImportLegacyMicrosoftAccountCommand.CanExecute(null));
            Assert.Null(fake.LastImportedLegacySource);
            var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LegacyMicrosoftAccountSelector");
            selector.SelectedItem = vm.Accounts.LegacyMicrosoftAccounts[1]; Pump(window);
            Assert.True(vm.Accounts.ImportLegacyMicrosoftAccountCommand.CanExecute(null));
            await vm.Accounts.ImportLegacyMicrosoftAccountCommand.ExecuteAsync(null); Pump(window);
            Assert.Equal("second", fake.LastImportedLegacySource);
            Assert.Empty(vm.Accounts.LegacyMicrosoftAccounts);
            Assert.False(vm.Accounts.IsAddingAccount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingDuringReadCancelsAndDiscardsLateChoices()
    {
        var result = new TaskCompletionSource<IReadOnlyList<LegacyMicrosoftAccountChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var fake = new FakeLauncherService { SupportsLegacyMicrosoftImport = true,
            LegacyReadBehavior = ct => { received = ct; return result.Task; } };
        using var vm = new MainWindowViewModel(fake, new FakeExternalLauncher());
        await vm.InitializeAsync();
        vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "microsoft");
        var read = vm.Accounts.ReadLegacyMicrosoftAccountsCommand.ExecuteAsync(null);
        Assert.True(vm.Accounts.IsAuthenticating);
        vm.SelectedPage = vm.Downloads;
        Assert.True(received.IsCancellationRequested);
        result.SetResult([new("late", "LateFixture")]);
        await read;
        Assert.Empty(vm.Accounts.LegacyMicrosoftAccounts);
        Assert.Null(vm.Accounts.SelectedLegacyMicrosoftAccount);
        Assert.False(vm.Accounts.IsAuthenticating);
    }

    [AvaloniaFact]
    public void PlatformWithoutReaderDoesNotOfferImport()
    {
        using var vm = new MainWindowViewModel(new FakeLauncherService(), new FakeExternalLauncher());
        Assert.False(vm.Accounts.SupportsLegacyMicrosoftImport);
        Assert.False(vm.Accounts.ReadLegacyMicrosoftAccountsCommand.CanExecute(null));
    }

    private static void Pump(Window window)
    { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
}
