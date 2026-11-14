using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using SHOU.Launcher.Desktop.Views;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CurseForgeExportUiTests
{
    private sealed class Service : FakeLauncherService, ICurseForgeExportService
    {
        internal ModrinthPackExportOptions? Options;
        public Task<string> ExportCurseForgePackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default)
        { Options = options; return Task.FromResult("controlled-cf.zip"); }
    }
    [AvaloniaFact]
    public async Task ExportButtonInvokesSharedServiceWithExplicitLocalFileChoice()
    {
        var service = new Service(); using var root = new MainWindowViewModel(service, new FakeExternalLauncher());
        var window = new MainWindow { DataContext = root }; window.Show();
        try
        {
            root.Instances.ShowInstanceSettingsCommand.Execute(null);
            root.Instances.SelectedSection = root.Instances.Sections.Single(s => s.Key == "export");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ExportCurseForgePackButton");
            Assert.True(button.IsEffectivelyEnabled);
            root.Instances.ExportPackName = "CF 中文包"; root.Instances.ExportPackVersion = "2.0";
            root.Instances.ExportPackLocalFiles = true;
            await root.Instances.ExportCurseForgePackCommand.ExecuteAsync(null);
            Assert.Equal("CF 中文包", service.Options!.Name); Assert.Equal("2.0", service.Options.VersionId);
            Assert.True(service.Options.BundleInstalledFiles);
            Assert.Contains("controlled-cf.zip", root.Instances.Feedback);
        }
        finally { window.Close(); }
    }
}
