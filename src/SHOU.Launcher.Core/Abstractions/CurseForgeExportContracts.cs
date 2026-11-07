namespace SHOU.Launcher.Core.Abstractions;

public interface ICurseForgeExportService
{
    Task<string> ExportCurseForgePackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default);
}
