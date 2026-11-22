using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class MicrosoftDeviceConvenienceTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceCodeCopiesAndOpensOnceWithoutCoveringAuthorizationControls(bool clipboardFails)
    {
        IProgress<DeviceCodeChallenge>? reporter = null;
        var fake = new FakeLauncherService { MicrosoftBehavior = async (progress, ct) =>
        {
            reporter = progress;
            var challenge = new DeviceCodeChallenge("FIXTURE-AUTO-CODE", new("https://microsoft.com/link"), DateTimeOffset.UtcNow.AddMinutes(5), "fixture");
            progress.Report(challenge); progress.Report(challenge);
            await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("unreachable");
        } };
        var browser = new FakeExternalLauncher(); using var vm = new MainWindowViewModel(fake, browser);
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 560 }; window.Show(); vm.ShowHomeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var copied = new List<string>();
        vm.Accounts.DeviceCodeClipboardWriter = (code, _) => { if (clipboardFails) throw new IOException("clipboard unavailable"); copied.Add(code); return Task.CompletedTask; };
        try
        {
            vm.Accounts.SelectedProvider = vm.Accounts.Providers.Single(p => p.Id == "microsoft");
            vm.Accounts.LoginMicrosoftCommand.Execute(null);
            for (var i = 0; i < 100 && browser.OpenedUris.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(new Uri("https://microsoft.com/link"), Assert.Single(browser.OpenedUris));
            Assert.Equal(clipboardFails ? 0 : 1, copied.Count); Assert.False(vm.ShowOperationStatus);
            Assert.Contains("浏览器", vm.Accounts.DeviceAutomationStatus); Assert.False(vm.Accounts.ShowMicrosoftLoginForm);
            var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelMicrosoftAuthorizationButton");
            Assert.True(cancel.IsEffectivelyVisible); Assert.True(cancel.IsEffectivelyEnabled);
            var accounts = window.GetVisualDescendants().OfType<AccountsView>().Single(v => v.IsEffectivelyVisible);
            var point = cancel.TranslatePoint(new Point(0, 0), accounts)!.Value;
            Assert.True(point.Y >= 0 && point.Y + cancel.Bounds.Height <= accounts.Bounds.Height, "Authorization cancel must remain inside the minimum home account viewport.");
            cancel.Command!.Execute(null); await vm.Accounts.LoginMicrosoftCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            reporter!.Report(new("LATE-CODE", new("https://microsoft.com/link"), DateTimeOffset.UtcNow.AddMinutes(5), "late")); Dispatcher.UIThread.RunJobs();
            Assert.Single(browser.OpenedUris); Assert.False(vm.Accounts.HasDeviceChallenge); Assert.False(vm.IsBusy);
            Assert.DoesNotContain("FIXTURE-AUTO-CODE", vm.Logs.ExportText);
        }
        finally { vm.CancelOperationCommand.Execute(null); window.Close(); }
    }
}
