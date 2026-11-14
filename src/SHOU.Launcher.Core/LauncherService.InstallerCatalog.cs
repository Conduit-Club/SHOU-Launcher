using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Loaders;
using SHOU.Launcher.Core.Minecraft;
using System.Collections.Concurrent;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IInstallerCatalogService
{
    private readonly ConcurrentDictionary<string, (InstallerVersion Version, string Destination)> installerTransfers = new();
    public async Task<InstallerCatalog> GetInstallerCatalogAsync(string loader, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await new InstallerCatalogReader(http).ReadAsync(loader, cancellationToken).ConfigureAwait(false);
    }
    public Task SaveInstallerAsync(InstallerVersion version, string destination, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择本机 JAR 文件保存位置。");
        var uri = InstallerCatalogReader.ArtifactUri(version);
        return ExecuteAsync("Installing", async ct =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            installCancellation = cancellation;
            var ownership = downloadOwners.Begin("installer-file:" + version.Loader + ":" + version.ArtifactVersion);
            installerTransfers[ownership.Generation] = (version, destination);
            try
            {
                using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
                await new InstallerCatalogReader(client).SaveAsync(version, destination, Paths.TempDirectory,
                    new InlineProgress<TransferProgress>(OnTransfer), cancellation.Token).ConfigureAwait(false);
            }
            finally { downloadOwners.End(ownership); installCancellation = null; }
        }, cancellationToken);
    }
}
