using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class ManualUpdateUiTests
{
    private sealed class Service : FakeLauncherService, ILauncherInformationService
    {
        public int Checks;
        public Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken cancellationToken = default)
        { Checks++; return Task.FromResult<IReadOnlyList<LauncherRelease>>([new("v2.0", "Fixture release", "Public notes", DateTimeOffset.UtcNow, new("https://github.com/Conduit-Club/SHOU-Launcher/releases/tag/v2.0"), false)]); }
        public Task<IReadOnlyList<LauncherFeedback>> GetLauncherFeedbackAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LauncherFeedback>>([]);
        public Task<string> ReadHomeDocumentAsync(string location, bool remote, CancellationToken cancellationToken = default) => Task.FromResult("");
    }
    [AvaloniaFact]
    public async Task StartupAndOpeningUpdatePageNeverCheckUntilUserInvokesManualAction()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        try
        {
            await Task.Delay(850); Dispatcher.UIThread.RunJobs(); Assert.Equal(0, service.Checks);
            root.SelectedPage = root.Settings; root.Settings.SelectedSection = "updates"; Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, service.Checks);
            await root.Settings.CheckUpdatesCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, service.Checks); Assert.True(root.Settings.OpenSelectedReleaseCommand.CanExecute(null));
            Assert.DoesNotContain(typeof(SettingsPageViewModel).GetProperties(), p => p.Name == "CheckUpdatesOnStartup");
        }
        finally { window.Close(); }
    }
}
