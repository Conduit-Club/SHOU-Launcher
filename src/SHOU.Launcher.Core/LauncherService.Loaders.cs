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
        var valid = instance.Loader switch
        {
            "fabric" => main is "net.fabricmc.loader.impl.launch.knot.KnotClient" or "net.fabricmc.loader.launch.knot.KnotClient",
            "quilt" => main == "org.quiltmc.loader.impl.launch.knot.KnotClient",
            "forge" or "neoforge" => main == "cpw.mods.bootstraplauncher.BootstrapLauncher",
            _ => false
        };
        if (!valid) throw new InvalidDataException("加载器主类与实例类型不匹配，未按原版启动。");
    }
    private OfficialLoaderInstaller OfficialLoaders => new(http, downloader, platform.Processes, Paths.CacheDirectory, Paths.TempDirectory, Sources, () => network.Capture().Proxy);
    public async Task<IReadOnlyList<LoaderVersionChoice>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (loader == "fabric") return (await Catalog.FabricAsync(gameVersion, cancellationToken)).Select(v => new LoaderVersionChoice(loader, v.Version, v.Stable)).ToArray();
        return await new LoaderCatalog(http).GetAsync(loader, gameVersion, cancellationToken).ConfigureAwait(false);
    }
    public async Task<GameInstance> CreateLoaderInstanceAsync(string name, string gameVersion, string loader, string loaderVersion, CancellationToken cancellationToken = default)
    {
        if (loader == "fabric") return await CreateFabricInstanceAsync(name, gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false);
        if (loader is not ("quilt" or "forge" or "neoforge")) throw new PlatformNotSupportedException("尚未实现此加载器的安装链。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("实例名称须为1–80字的单行文字。");
        SafePath.Identifier(gameVersion); SafePath.Identifier(loaderVersion); GameInstance? created = null;
        await ExecuteAsync("Installing", async ct =>
        {
            var profile = loader == "quilt" ? await CacheQuiltProfileAsync(gameVersion, loaderVersion, ct).ConfigureAwait(false)
                : (await OfficialLoaders.CacheAsync(loader, gameVersion, loaderVersion, ct).ConfigureAwait(false)).ProfileId;
            created = new(Guid.NewGuid().ToString("N"), name.Trim(), profile,
                SafePath.Within(Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory, Guid.NewGuid().ToString("N")), Loader: loader, LoaderVersion: loaderVersion, GameVersionId: gameVersion);
            Directory.CreateDirectory(created.GameDirectory);
            var candidate = Snapshot with { Instances = Snapshot.Instances.Append(created).ToArray(), SelectedInstanceId = created.Id };
            await PersistCandidateAsync(candidate, ct).ConfigureAwait(false);
            Set(s => s with { Instances = candidate.Instances, SelectedInstanceId = created.Id });
        }, cancellationToken).ConfigureAwait(false);
        return created!;
    }
    private async Task<string> CacheQuiltProfileAsync(string gameVersion, string loaderVersion, CancellationToken ct)
    {
        var text = await new LoaderCatalog(http).TextAsync(new("https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(gameVersion) + "/" + Uri.EscapeDataString(loaderVersion) + "/profile/json"), 4 * 1024 * 1024, ct);
        var profile = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Quilt配置不是对象。");
        var id = SafePath.Identifier((string?)profile["id"] ?? throw new InvalidDataException("Quilt配置缺少ID。"));
        if ((string?)profile["inheritsFrom"] != gameVersion || !id.Contains(loaderVersion, StringComparison.Ordinal) || (string?)profile["mainClass"] != "org.quiltmc.loader.impl.launch.knot.KnotClient")
            throw new InvalidDataException("Quilt配置身份不匹配。");
        _ = VersionMetadataResolver.ParseVersion(text, id);
        var path = SafePath.Within(Paths.CacheDirectory, "versions/" + id + "/" + id + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temporary, text, ct); File.Move(temporary, path, true); await File.WriteAllTextAsync(path + ".sha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), ct); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return id;
    }
}
