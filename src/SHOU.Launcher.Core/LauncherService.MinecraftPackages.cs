using System.Collections.Concurrent;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IMinecraftPackageService
{
    private readonly ConcurrentDictionary<string, (string Version, string Kind, string Destination)> packageTransfers = new();
    private async Task<IReadOnlyList<(MinecraftPackage File, Uri Source)>> ReadPackagesAsync(string version, CancellationToken ct)
    {
        await InitializeAsync(ct).ConfigureAwait(false);
        var resolved = await new VersionMetadataResolver(http, downloader, Paths.CacheDirectory, Sources).ResolveAsync(version, ct).ConfigureAwait(false);
        return MinecraftPackages.Read(version, resolved.Metadata);
    }
    public async Task<IReadOnlyList<MinecraftPackage>> GetMinecraftPackagesAsync(string version, CancellationToken ct = default)
        => (await ReadPackagesAsync(version, ct).ConfigureAwait(false)).Select(p => p.File).ToArray();
    public Task DownloadMinecraftPackageAsync(string version, string kind, string destination, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择本机 JAR 文件保存位置。");
        return ExecuteAsync("Installing", async token =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            installCancellation = cancellation;
            var ownership = downloadOwners.Begin("minecraft-package:" + version + ":" + kind);
            packageTransfers[ownership.Generation] = (version, kind, destination);
            try
            {
                var files = await ReadPackagesAsync(version, cancellation.Token).ConfigureAwait(false);
                var selected = files.SingleOrDefault(f => f.File.Kind == kind);
                if (selected.File is null) throw new InvalidOperationException("此版本没有可下载的所选文件。");
                // A dedicated transport follows no redirects and carries no account/API headers.
                using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
                await new VerifiedDownloader(client).DownloadAsync(new([selected.Source], destination, selected.File.Sha1, Size: selected.File.Size),
                    new InlineProgress<TransferProgress>(OnTransfer), cancellation.Token).ConfigureAwait(false);
            }
            finally { downloadOwners.End(ownership); installCancellation = null; }
        }, ct);
    }
}
