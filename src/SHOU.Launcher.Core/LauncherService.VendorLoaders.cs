using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.Loaders;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    private PortableVendorInstaller VendorInstaller(HttpClient client) => new(client, downloader, platform.Processes, Paths.CacheDirectory, Paths.TempDirectory, Sources, () => network.Capture().Proxy, javaBootstrap);
    private async Task<string> CacheVendorProfileAsync(string loader, string game, string version, CancellationToken ct)
    {
        using var client = new HttpClient(new ConfigurableNetworkHandler(network, allowAutoRedirect: false));
        var installer = VendorInstaller(client);
        if (await installer.CachedProfileAsync(loader, game, version, ct).ConfigureAwait(false) is { } cached) return cached;
        var vanilla = await new VersionMetadataResolver(http, downloader, Paths.CacheDirectory, Sources).ResolveAsync(game, ct).ConfigureAwait(false);
        var javaVersion = vanilla;
        if (loader == "labymod")
        {
            var channel = version.StartsWith("production-", StringComparison.Ordinal) ? "production" : version.StartsWith("snapshot-", StringComparison.Ordinal) ? "snapshot" : throw new InvalidDataException("LabyMod 发布渠道身份无效。");
            var text = await new LoaderCatalog(http).TextAsync(new("https://releases.r2.labymod.net/api/v1/manifest/" + channel + "/latest.json"), 2 * 1024 * 1024, ct).ConfigureAwait(false);
            var target = (JsonNode.Parse(text)?["minecraftVersions"] as JsonArray)?.SingleOrDefault(v => (string?)v?["version"] == game) ?? throw new InvalidDataException("LabyMod 不支持所选 Minecraft 版本。");
            if (target["runtime"]?["version"] is { } major && target["runtime"]?["name"] is { } component)
            {
                var metadata = (JsonObject)vanilla.Metadata.DeepClone(); metadata["javaVersion"] = new JsonObject { ["majorVersion"] = major.GetValue<int>(), ["component"] = component.GetValue<string>() };
                javaVersion = vanilla with { Metadata = metadata };
            }
        }
        var subject = new GameInstance("vendor-install", "安装适配器", game, Paths.TempDirectory, GameVersionId: game);
        var java = await SelectInstanceJavaAsync(subject, javaVersion, ct).ConfigureAwait(false);
        return await installer.EnsureAsync(loader, game, version, javaVersion, java, ct).ConfigureAwait(false);
    }
    private async Task<IReadOnlyList<LoaderVersionChoice>> VendorChoicesAsync(string loader, string game, CancellationToken ct)
    {
        var catalog = await new InstallerCatalogReader(http).ReadAsync(loader, ct).ConfigureAwait(false);
        var result = new List<LoaderVersionChoice>();
        foreach (var row in catalog.Versions)
        {
            if (loader != "labymod" && row.GameVersion != game) continue;
            if (loader == "labymod")
            {
                var text = await new LoaderCatalog(http).TextAsync(new("https://releases.r2.labymod.net/api/v1/manifest/" + row.Channel + "/latest.json"), 2 * 1024 * 1024, ct).ConfigureAwait(false);
                var metadata = JsonNode.Parse(text);
                if ((metadata?["minecraftVersions"] as JsonArray)?.Any(v => (string?)v?["version"] == game) != true) continue;
            }
            result.Add(new(loader, PortableVendorInstaller.VersionKey(row), row.Channel is "stable" or "production", Caption: row.Version + (loader == "labymod" ? row.Channel == "production" ? " · 稳定版" : " · 快照版" : "")));
        }
        return result;
    }
}
