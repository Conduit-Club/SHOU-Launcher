using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ICurseForgeExportService
{
    public async Task<string> ExportCurseForgePackAsync(string instanceId, ModrinthPackExportOptions options, CancellationToken cancellationToken = default)
    {
        options = options with { OverridePaths = options.OverridePaths?.ToArray() };
        string? result = null;
        await ExecuteAsync("Ready", async ct =>
        {
            var instance = ContentTarget(instanceId, true);
            var plan = await CurseForgePackExport.PlanAsync(instance, options, ct).ConfigureAwait(false);
            if (plan.References.Count > 0)
            {
                using var client = localPackResolver is null ? new HttpClient(new Downloads.ConfigurableNetworkHandler(network, allowAutoRedirect: false)) : null;
                var resolver = localPackResolver ?? new CurseForgePackResolver(client!, build.CurseForgeApiKey);
                var official = await resolver.ResolveAsync(plan.References, Instances.InstanceIdentity.GameVersion(instance), instance.Loader, ct).ConfigureAwait(false);
                if (official.BlockedFiles.Count > 0 || official.Files.Count != plan.References.Count) throw new InvalidDataException("CurseForge 文件身份未通过官方核实，未生成导出包。");
                var installed = await ContentInstallFiles.ReadManifestAsync(instance, ct).ConfigureAwait(false);
                foreach (var file in official.Files)
                {
                    var recorded = installed.Files.Single(f => f.ProjectId == "cf-" + file.ProjectId && f.ReleaseId == "cf-" + file.FileId);
                    if (recorded.RelativePath != file.Path || recorded.Size != file.Size || !recorded.Sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)
                        || file.Sha512 is not null && !recorded.Sha512.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CurseForge 项目/文件身份与本地资源不一致，未生成导出包。");
                }
            }
            result = await ShouPackArchive.ExportAsync(instance, SafePath.Within(Paths.CacheDirectory, "exports"), false, ct, plan).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
