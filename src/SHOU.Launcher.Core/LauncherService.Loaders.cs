using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Loaders;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : ILoaderInstanceService
{
    private static void ValidateLoaderProfile(GameInstance instance, ResolvedVersion resolved)
    {
        if (instance.Loader == "vanilla") return;
        var game = Instances.InstanceIdentity.GameVersion(instance);
        if (!resolved.InheritanceChain.Contains(game, StringComparer.Ordinal)) throw new InvalidDataException("加载器配置未继承所选Minecraft版本。");
        var main = (string?)resolved.Metadata["mainClass"];
        if (Loaders.PortableVendorInstaller.Supports(instance.Loader)) { Loaders.PortableVendorInstaller.ValidateProfile(instance.Loader, resolved.Metadata); return; }
        var valid = instance.Loader switch
        {
            "fabric" or "legacyfabric" => main is "net.fabricmc.loader.impl.launch.knot.KnotClient" or "net.fabricmc.loader.launch.knot.KnotClient",
            "quilt" => main == "org.quiltmc.loader.impl.launch.knot.KnotClient",
            "forge" => main is "cpw.mods.bootstraplauncher.BootstrapLauncher" or "cpw.mods.modlauncher.Launcher" or "net.minecraftforge.bootstrap.ForgeBootstrap",
            "neoforge" => main == "cpw.mods.bootstraplauncher.BootstrapLauncher",
            "cleanroom" => main == "top.outlands.foundation.boot.Foundation" && game == "1.12.2" && resolved.RequiredJavaMajor == 25,
            _ => false
        };
        if (!valid) throw new InvalidDataException("加载器主类与实例类型不匹配，未按原版启动。");
    }
    private OfficialLoaderInstaller OfficialLoaders => new(http, downloader, platform.Processes, Paths.CacheDirectory, Paths.TempDirectory, Sources, () => network.Capture().Proxy);
    private async Task<string> CacheLoaderProfileAsync(string loader, string game, string? version, CancellationToken ct)
    {
        if (PortableVendorInstaller.Supports(loader)) return await CacheVendorProfileAsync(loader, game, version ?? throw new InvalidDataException("加载器版本缺失。"), ct).ConfigureAwait(false);
        if (loader is "cleanroom" or "legacyfabric") IO.SafePath.Identifier(version ?? throw new InvalidDataException("加载器版本缺失。"));
        else Content.PackLoaderIdentity.Validate(loader, version);
        return loader switch
        {
            "vanilla" => game,
            "fabric" => await new FabricMetadataProvider(http, Paths.CacheDirectory, Sources).CacheProfileAsync(game, version!, ct).ConfigureAwait(false),
            "quilt" => await CacheQuiltProfileAsync(game, version!, ct).ConfigureAwait(false),
            "legacyfabric" => await CacheQuiltProfileAsync(game, version!, ct, loader).ConfigureAwait(false),
            _ => (await OfficialLoaders.CacheAsync(loader, game, version!, ct).ConfigureAwait(false)).ProfileId
        };
    }
    public async Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (loader == "fabric") return (await Catalog.FabricAsync(gameVersion, cancellationToken)).Select(v => new LoaderVersionChoice(loader, v.Version, v.Stable)).ToArray();
        if (PortableVendorInstaller.Supports(loader)) return await VendorChoicesAsync(loader, gameVersion, cancellationToken).ConfigureAwait(false);
        if (loader == "cleanroom") return gameVersion != "1.12.2" ? [] : (await new InstallerCatalogReader(http).ReadAsync(loader, cancellationToken).ConfigureAwait(false)).Versions
            .Select(v => new LoaderVersionChoice(loader, v.Version, v.Channel == "stable", CanInstall: true)).ToArray();
        return await new LoaderCatalog(http).GetAsync(loader, gameVersion, cancellationToken).ConfigureAwait(false);
    }
    public async Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion, CancellationToken cancellationToken = default)
    {
        if (loader == "fabric") return await CreateFabricInstanceAsync(name, gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false);
        if (loader is not ("quilt" or "forge" or "neoforge" or "cleanroom" or "legacyfabric") && !PortableVendorInstaller.Supports(loader)) throw new PlatformNotSupportedException("尚未实现此加载器的安装链。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("实例名称须为1–80字的单行文字。");
        SafePath.Identifier(gameVersion); SafePath.Identifier(loaderVersion); GameInstance? created = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var profile = await CacheLoaderProfileAsync(loader, gameVersion, loaderVersion, ct).ConfigureAwait(false);
            created = new(Guid.NewGuid().ToString("N"), name.Trim(), profile,
                NewGameDirectory(loader), Loader: loader, LoaderVersion: loaderVersion, GameVersionId: gameVersion);
            await RegisterNewInstanceAsync(created, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return created!;
    }
    private async Task<string> CacheQuiltProfileAsync(string gameVersion, string loaderVersion, CancellationToken ct, string loader = "quilt")
    {
        var endpoint = loader == "quilt" ? "https://meta.quiltmc.org/v3" : "https://meta.legacyfabric.net/v2";
        var main = loader == "quilt" ? "org.quiltmc.loader.impl.launch.knot.KnotClient" : "net.fabricmc.loader.impl.launch.knot.KnotClient";
        var text = await new LoaderCatalog(http).TextAsync(new(endpoint + "/versions/loader/" + Uri.EscapeDataString(gameVersion) + "/" + Uri.EscapeDataString(loaderVersion) + "/profile/json"), 4 * 1024 * 1024, ct);
        var profile = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Quilt配置不是对象。");
        var id = SafePath.Identifier((string?)profile["id"] ?? throw new InvalidDataException("Quilt配置缺少ID。"));
        if ((string?)profile["inheritsFrom"] != gameVersion || !id.Contains(loaderVersion, StringComparison.Ordinal) || (string?)profile["mainClass"] != main)
            throw new InvalidDataException("Quilt配置身份不匹配。");
        _ = VersionMetadataResolver.ParseVersion(text, id);
        var relative = "versions/" + id + "/" + id + ".json";
        var path = Content.ContentInstallFiles.Literal(Paths.CacheDirectory, relative);
        var sidecar = Content.ContentInstallFiles.Literal(Paths.CacheDirectory, relative + ".sha256");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, ct); Downloads.VerifiedDownloader.RejectLink(path); File.Move(temporary, path, true);
            var hashStage = sidecar + ".tmp-" + Guid.NewGuid().ToString("N");
            try { await File.WriteAllTextAsync(hashStage, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), ct); Downloads.VerifiedDownloader.RejectLink(sidecar); File.Move(hashStage, sidecar, true); }
            finally { if (File.Exists(hashStage)) File.Delete(hashStage); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return id;
    }
}
