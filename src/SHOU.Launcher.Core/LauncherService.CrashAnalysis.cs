using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Diagnostics;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ICrashAnalysisService
{
    public Task<CrashAnalysisReport> AnalyzeCrashFilesAsync(IReadOnlyList<string> files, string language = "zh-CN", CancellationToken cancellationToken = default)
        => CrashAnalysis.AnalyzeAsync(files, language, ct: cancellationToken, redactor: redactor);
    public async Task<CrashAnalysisReport> AnalyzeInstanceCrashAsync(string instanceId, string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var instance = Snapshot.Instances.Single(i => i.Id == instanceId);
        return await AnalyzeInstanceCrashAsync(instance, language, null, cancellationToken);
    }
    private Task<CrashAnalysisReport> AnalyzeInstanceCrashAsync(GameInstance instance, string language, string? output, CancellationToken ct, DateTime? since = null)
    {
        var files = new List<string>();
        foreach (var directory in new[] { instance.GameDirectory, Path.Combine(instance.GameDirectory, "logs"), Path.Combine(instance.GameDirectory, "crash-reports") })
        {
            IO.SafePath.RejectLinks(directory); if (!Directory.Exists(directory)) continue;
            var candidates = Directory.EnumerateFiles(directory).Where(p => Path.GetFileName(p) is "latest.log" or "debug.log" || Path.GetFileName(p).StartsWith("crash-", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("hs_err", StringComparison.Ordinal));
            files.AddRange(candidates.Where(p => since is null || File.GetLastWriteTimeUtc(p) >= since.Value.AddSeconds(-2)).GroupBy(p => Path.GetFileName(p).StartsWith("crash-", StringComparison.Ordinal) ? "crash" : Path.GetFileName(p).StartsWith("hs_err", StringComparison.Ordinal) ? "hs_err" : Path.GetFileName(p)).Select(g => g.OrderByDescending(File.GetLastWriteTimeUtc).First()));
        }
        if (output is not null) files.Add(output);
        return CrashAnalysis.AnalyzeAsync(files, language, [Path.Combine(instance.GameDirectory, "mods")], ct, redactor, output is not null, instance.Id);
    }
    public Task ExportCrashAnalysisAsync(CrashAnalysisReport report, string destination, CancellationToken cancellationToken = default)
        => CrashAnalysis.ExportAsync(report, destination, cancellationToken);
}
