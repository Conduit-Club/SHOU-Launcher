namespace SHOU.Launcher.Core.Abstractions;

public sealed record CrashLogSource(string Name, string Kind, string Text, string? Path = null);
public sealed record CrashDiagnosis(string Cause, string Confidence, IReadOnlyList<string> Evidence);
public sealed record CrashAnalysisReport(string Text, IReadOnlyList<CrashDiagnosis> Findings, IReadOnlyList<string> Files, bool SuggestInstanceSettings = false, string? InstanceId = null, IReadOnlyList<CrashLogSource>? Sources = null);
public interface ICrashAnalysisService
{
    Task<CrashAnalysisReport> AnalyzeCrashFilesAsync(IReadOnlyList<string> files, string language = "zh-CN", CancellationToken cancellationToken = default);
    Task<CrashAnalysisReport> AnalyzeInstanceCrashAsync(string instanceId, string language = "zh-CN", CancellationToken cancellationToken = default);
    Task ExportCrashAnalysisAsync(CrashAnalysisReport report, string destination, CancellationToken cancellationToken = default);
}
