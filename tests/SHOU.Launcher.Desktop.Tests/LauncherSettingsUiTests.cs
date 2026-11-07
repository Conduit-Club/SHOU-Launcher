using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.Localization;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class LauncherSettingsUiTests
{
    [AvaloniaFact]
    public void FirstVisitOpensMicrosoftFormAndEachEmptyChannelOpensInOneClick()
    {
        var service = new FakeLauncherService(); service.Publish(service.Snapshot with { Accounts = [], SelectedAccountId = null }); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        Assert.Equal("microsoft", root.Accounts.SelectedProvider.Id); Assert.True(root.Accounts.IsAddingAccount);
        root.Accounts.SelectProviderCommand.Execute("offline"); Assert.True(root.Accounts.IsAddingAccount); Assert.True(root.Accounts.IsOffline);
        root.Accounts.EndAddAccountCommand.Execute(null); root.Accounts.SelectProviderCommand.Execute("offline"); Assert.True(root.Accounts.IsAddingAccount);
        root.Accounts.SelectProviderCommand.Execute("mua"); Assert.True(root.Accounts.IsAddingAccount); Assert.True(root.Accounts.IsYggdrasil);
    }
    [AvaloniaFact]
    public void LateAccountLoadingPreservesExistingIdentityAndExplicitChannelChoice()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var profile = new AccountProfile("old", AccountKind.Offline, "settings-fixture", new string('1', 32));
        service.Publish(service.Snapshot with { Accounts = [profile], SelectedAccountId = profile.Id });
        Assert.Equal("offline", root.Accounts.SelectedProvider.Id); Assert.True(root.Accounts.ShowSelectedAccount); Assert.False(root.Accounts.IsAddingAccount);
        root.Accounts.SelectProviderCommand.Execute("microsoft"); service.Publish(service.Snapshot with { Accounts = [profile] });
        Assert.Equal("microsoft", root.Accounts.SelectedProvider.Id); Assert.True(root.Accounts.IsAddingAccount);
    }
    [AvaloniaFact]
    public async Task SavedLanguageChangesActualCaptionsAndKeepsUserNamesAndRegionIndependent()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show(); root.ShowSettingsCommand.Execute(null); root.Settings.SelectedSection = "language";
        try
        {
            root.Settings.Language = "en-US"; root.Settings.RegionalFormat = "fr-FR"; await root.Settings.SaveCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var navigation = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsAppearanceNavigation");
            Assert.Equal("Personalization", navigation.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.Equal("en-US", UiLocalization.Instance.Locale); Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            var profile = new AccountProfile("literal", AccountKind.Offline, "设置", new string('2', 32)); service.Publish(service.Snapshot with { Accounts = [profile], SelectedAccountId = profile.Id });
            Assert.Equal("设置", root.Accounts.SelectedPlayerName);
            using var restarted = new MainWindowViewModel(service, new FakeExternalLauncher()); Assert.Equal("en-US", restarted.Settings.Language); Assert.Equal("fr-FR", restarted.Settings.RegionalFormat);
        }
        finally { root.Settings.Language = "zh-CN"; root.Settings.RegionalFormat = "system"; await root.Settings.SaveCommand.ExecuteAsync(null); window.Close(); UiLocalization.Instance.SetLanguage("zh-CN"); }
    }
    [AvaloniaFact]
    public async Task AppearanceAndLauncherBehaviorPersistAndWindowLockActuallyApplies()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        try
        {
            root.Settings.LockWindowSize = true; root.Settings.LightAccent = "garnet"; root.Settings.TitleMode = "text"; root.Settings.CustomTitleText = "R16 shell fixture";
            root.Settings.LogLineLimit = 150; root.Settings.DisableHardwareAcceleration = true; root.Settings.UseDnsOverHttps = true; root.Settings.CheckUpdatesOnStartup = true;
            await root.Settings.SaveCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.False(window.CanResize); Assert.False(window.CanMaximize); Assert.Equal("R16 shell fixture", window.FindControl<TextBlock>("WindowBrandTitle")!.Text);
            var saved = new SettingsPresentationStore(service.Paths.DataDirectory).Load(); Assert.True(saved.LockWindowSize); Assert.Equal("garnet", saved.LightAccent);
            Assert.Equal(150, service.Snapshot.Settings.Launcher!.LogLineLimit); Assert.True(service.Snapshot.Settings.Launcher.DisableHardwareAcceleration);
            for (var i = 0; i < 170; i++) service.EmitLog("line " + i);
            Assert.Equal(150, root.Logs.Entries.Count);
        }
        finally { root.Settings.LockWindowSize = false; root.Settings.LightAccent = "default"; root.Settings.TitleMode = "default"; await root.Settings.SaveCommand.ExecuteAsync(null); window.Close(); }
    }
    [AvaloniaFact]
    public async Task SettingsExportExcludesSecretsAndImportIsOnlyADraftUntilSaved()
    {
        var service = new FakeLauncherService(); service.Publish(service.Snapshot with { Settings = service.Snapshot.Settings with { MicrosoftClientId = "11111111-2222-3333-4444-555555555555", Proxy = new(ProxyMode.Direct, CredentialKey: "credential-fixture") } });
        using var root = new MainWindowViewModel(service, new FakeExternalLauncher()); var bytes = root.Settings.ExportSettings(); var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("credential-fixture", text); Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", text); Assert.DoesNotContain("accounts", text, StringComparison.OrdinalIgnoreCase);
        var node = JsonNode.Parse(bytes)!.AsObject(); node["settings"]!["memoryMiB"] = 4096; node["settings"]!["proxy"]!["credentialKey"] = "injected-key";
        root.Settings.ImportSettings(Encoding.UTF8.GetBytes(node.ToJsonString())); Assert.True(root.Settings.IsDirty); Assert.Equal(0, service.SaveCalls); Assert.Equal(4096, root.Settings.MemoryMiB);
        await root.Settings.SaveCommand.ExecuteAsync(null); Assert.Equal("credential-fixture", service.Snapshot.Settings.Proxy!.CredentialKey); Assert.Equal(1, service.SaveCalls);
    }
    [AvaloniaFact]
    public async Task ClosingWaitsForSettingsCancellationAndRejectsASecondConcurrentExport()
    {
        var service = new FakeLauncherService(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = root.Settings.RunFileOperationAsync(async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cleanupStarted.SetResult(); await allowCleanup.Task; }
        });
        await entered.Task; Assert.True(root.Settings.IsLauncherBusy);
        Assert.False(await root.Settings.RunFileOperationAsync(_ => throw new InvalidOperationException("Concurrent work must not start.")));
        var closing = root.Settings.CancelAndWaitAsync(); await cleanupStarted.Task;
        Assert.False(closing.IsCompleted); allowCleanup.SetResult();
        await closing; Assert.False(await work); Assert.False(root.Settings.IsLauncherBusy);
    }
    [AvaloniaFact]
    public async Task PresentationStoreRetainsFutureFieldsAndWritesABackup()
    {
        var service = new FakeLauncherService(); var store = new SettingsPresentationStore(service.Paths.DataDirectory); Directory.CreateDirectory(service.Paths.DataDirectory);
        File.WriteAllText(store.FilePath, "{\"futureField\":{\"retained\":true},\"showClubServers\":false}");
        await store.SaveAsync(store.Load() with { LockWindowSize = true });
        Assert.True(JsonNode.Parse(File.ReadAllText(store.FilePath))!["futureField"]!["retained"]!.GetValue<bool>()); Assert.True(File.Exists(store.FilePath + ".bak")); Assert.False(store.Load().ShowClubServers);
        Assert.Throws<InvalidDataException>(() => { File.WriteAllText(store.FilePath, "{\"fontFamily\":null}"); store.Load(); });
    }
}
