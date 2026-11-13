using Avalonia.Headless.XUnit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class CrashAnalysisUiTests
{
    [AvaloniaFact]
    public async Task ToolboxAnalyzesAndExportsAndAnOldAutomaticReportDoesNotReplaceManualResult()
    {
        var service = new Service(); using var vm = new MainWindowViewModel(service, new FakeExternalLauncher());
        vm.ShowToolsCommand.Execute(null); vm.Tools.ShowToolboxCommand.Execute(null);
        var automatic = new CrashAnalysisReport("automatic failure", [], []);
        service.Publish(service.Snapshot with { LastCrash = automatic });
        Assert.Contains("automatic failure", vm.Tools.Crash.AnalysisText);
        await vm.Tools.Crash.AnalyzeFilesAsync(["/fixture/latest.log"]);
        Assert.True(vm.Tools.Crash.HasReport); Assert.Contains("manual diagnosis", vm.Tools.Crash.AnalysisText);
        service.Publish(service.Snapshot with { LastCrash = automatic });
        Assert.Contains("manual diagnosis", vm.Tools.Crash.AnalysisText);
        await vm.Tools.Crash.ExportAsync("/fixture/report.zip"); Assert.Equal("/fixture/report.zip", service.Exported);
    }
    private sealed class Service : FakeLauncherService, ICrashAnalysisService
    {
        public string? Exported;
        public Task<CrashAnalysisReport> AnalyzeCrashFilesAsync(IReadOnlyList<string> files, string language = "zh-CN", CancellationToken cancellationToken = default) => Task.FromResult(new CrashAnalysisReport("manual diagnosis", [], files));
        public Task<CrashAnalysisReport> AnalyzeInstanceCrashAsync(string id, string language = "zh-CN", CancellationToken cancellationToken = default) => AnalyzeCrashFilesAsync([]);
        public Task ExportCrashAnalysisAsync(CrashAnalysisReport report, string destination, CancellationToken cancellationToken = default) { Exported = destination; return Task.CompletedTask; }
    }
}
