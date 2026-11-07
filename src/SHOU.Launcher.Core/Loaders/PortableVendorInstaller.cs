using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Loaders;

internal sealed record VendorInstallReceipt(string Loader, string Game, string Version, string Artifact, string InstallerHash, string ProfileHash,
    IReadOnlyDictionary<string, string> Libraries, IReadOnlyDictionary<string, string> Payload);

internal sealed class PortableVendorInstaller(HttpClient http, IVerifiedDownloader downloader, IProcessRunner processes, string cache, string temporary,
    Func<IReadOnlyList<PCL.Core.Conduit.DownloadSource>> sources, Func<ProxySettings> proxy, JavaBootstrapArtifact? bridge)
{
    internal static bool Supports(string loader) => loader is "optifine" or "labymod" or "liteloader";
    internal static string VersionKey(InstallerVersion row) => row.Loader == "labymod" ? row.Channel + "-" + row.Version + "-" + row.ArtifactVersion : row.Version;
    private OfficialLoaderInstaller Files => new(http, downloader, processes, cache, temporary, sources, proxy);
    private string Root(string loader, string game, string version) => ContentInstallFiles.Literal(cache, $"loaders/vendor/{SafePath.Identifier(loader)}/{SafePath.Identifier(game)}/{SafePath.Identifier(version)}");
    private string Profile(string id) => ContentInstallFiles.Literal(cache, $"versions/{SafePath.Identifier(id)}/{id}.json");
    internal async Task<string?> CachedProfileAsync(string loader, string game, string version, CancellationToken ct)
    {
        var root = Root(loader, game, version); var id = SafePath.Identifier($"shou-{loader}-{game}-{version}");
        return await ValidAsync(Path.Combine(root, "installed.json"), Path.Combine(root, "installer.jar"), Profile(id), loader, game, version, ct).ConfigureAwait(false) ? id : null;
    }
    internal async Task<string> EnsureAsync(string loader, string game, string version, ResolvedVersion vanilla, JavaInstallation java, CancellationToken ct)
    {
        var root = Root(loader, game, version); var id = SafePath.Identifier($"shou-{loader}-{game}-{version}");
        var recipe = Path.Combine(root, "installed.json"); var installer = Path.Combine(root, "installer.jar");
        if (await CachedProfileAsync(loader, game, version, ct).ConfigureAwait(false) is { } cached) return cached;
        if (proxy() is { Mode: ProxyMode.Custom, CredentialKey: { Length: > 0 } }) throw new NotSupportedException("此安装适配器暂不支持需要认证的 HTTP 代理。");
        var rows = (await new InstallerCatalogReader(http).ReadAsync(loader, ct).ConfigureAwait(false)).Versions;
        var row = rows.SingleOrDefault(r => VersionKey(r) == version && (loader == "labymod" || r.GameVersion == game)) ?? throw new InvalidDataException("所选安装器版本已不在目录，请重新选择。");
        Directory.CreateDirectory(root);
        await new InstallerCatalogReader(http).SaveAsync(row, installer, temporary, null, ct).ConfigureAwait(false);
        var stage = ContentInstallFiles.Literal(temporary, "vendor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var sourceGuard = Instances.WorldPathSnapshot.Capture(cache, installer);
            var installerHash = await DigestAsync(installer, true, ct);
            var installerInput = Path.Combine(stage, "vendor-installer.jar"); File.Copy(installer, installerInput);
            sourceGuard.Verify(); if (!await OfficialLoaderInstaller.HashMatchesAsync(installerInput, installerHash, true, ct)) throw new IOException("安装器在复制时发生变化。");
            var baseFolder = ContentInstallFiles.Literal(stage, "versions/" + game); Directory.CreateDirectory(baseFolder);
            await OfficialLoaderInstaller.AtomicJsonAsync(Path.Combine(baseFolder, game + ".json"), vanilla.Metadata, ct).ConfigureAwait(false);
            var client = vanilla.Metadata["downloads"]?["client"] as JsonObject ?? throw new InvalidDataException("缺少基础客户端身份。");
            var baseJar = ContentInstallFiles.Literal(cache, "versions/" + game + "/" + game + ".jar");
            var urls = PCL.Core.Conduit.DownloadSources.Resolve([(string)client["url"]!], sources()).Select(u => new Uri(u.Url)).ToArray();
            await downloader.DownloadAsync(new(urls, baseJar, (string?)client["sha1"], Size: (long?)client["size"]), cancellationToken: ct).ConfigureAwait(false);
            File.Copy(baseJar, Path.Combine(baseFolder, game + ".jar"));
            if (!await OfficialLoaderInstaller.HashMatchesAsync(Path.Combine(baseFolder, game + ".jar"), (string?)client["sha1"], false, ct)) throw new IOException("基础客户端在复制时发生变化。");
            await File.WriteAllTextAsync(Path.Combine(stage, "launcher_profiles.json"), "{\"profiles\":{}}", ct).ConfigureAwait(false);
            JsonObject profile;
            if (loader == "liteloader") profile = await ExtractLiteAsync(installerInput, game, stage, ct).ConfigureAwait(false);
            else
            {
                if (bridge is null) throw new InvalidOperationException("缺少已校验的安装适配器，请使用完整候选包。");
                Downloads.VerifiedDownloader.RejectLink(bridge.JarPath);
                if (new FileInfo(bridge.JarPath).Length > 2 * 1024 * 1024 || !await OfficialLoaderInstaller.HashMatchesAsync(bridge.JarPath, bridge.Sha256, true, ct)) throw new InvalidDataException("安装适配器哈希无效。");
                var helper = Path.Combine(stage, "installer-bridge.jar"); File.Copy(bridge.JarPath, helper);
                if (!await OfficialLoaderInstaller.HashMatchesAsync(helper, bridge.Sha256, true, ct)) throw new IOException("复制后的安装适配器发生变化。");
                var home = Path.Combine(stage, "home"); Directory.CreateDirectory(home);
                var args = new List<string> { "-Djava.awt.headless=true", "-Duser.home=" + home, "-Djava.io.tmpdir=" + home };
                var settings = proxy();
                if (settings.Mode == ProxyMode.System) args.Add("-Djava.net.useSystemProxies=true");
                if (settings.Mode == ProxyMode.Custom) { var address = new Uri(settings.Address!); args.AddRange(["-Dhttps.proxyHost=" + address.Host, "-Dhttps.proxyPort=" + address.Port]); }
                args.AddRange(["-cp", helper + Path.PathSeparator + installerInput, "org.shou.launcher.bootstrap.InstallerBridge", loader, stage, game, loader == "optifine" ? row.ArtifactVersion : row.Version, row.ArtifactVersion, row.Channel ?? "stable"]);
                var env = new Dictionary<string, string?> { ["JAVA_HOME"] = java.JavaHome, ["JAVA_TOOL_OPTIONS"] = null, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null, ["CLASSPATH"] = null, ["APPDATA"] = home, ["LOCALAPPDATA"] = home, ["XDG_CACHE_HOME"] = home, ["XDG_DATA_HOME"] = home };
                foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("SHOU_") || k.StartsWith("PCL_") || k is "CLIENT_ID" or "MS_CLIENT_ID" or "CURSEFORGE_API" || new[] { "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL", "COOKIE", "API_KEY", "APIKEY" }.Any(p => k.Contains(p, StringComparison.OrdinalIgnoreCase)))) env[key] = null;
                var result = await Files.RunWithStageBudgetAsync(new(java.ExecutablePath, args, stage, env, TimeSpan.FromMinutes(8), 64 * 1024), stage, ct).ConfigureAwait(false);
                if (result.Cancelled) throw new OperationCanceledException(ct);
                if (result.ExitCode != 0 || result.TimedOut || loader != "optifine" && !result.StandardOutput.Contains("SHOU_INSTALLER_COMPLETE", StringComparison.Ordinal)) throw new IOException($"安装适配器未完成（{loader}, exit={result.ExitCode}, timeout={result.TimedOut}）。" + (result.StandardError.Length > 1200 ? result.StandardError[^1200..] : result.StandardError));
                var profiles = Directory.GetFiles(Path.Combine(stage, "versions"), "*.json", SearchOption.AllDirectories).Where(p => p != Path.Combine(baseFolder, game + ".json")).ToArray();
                if (profiles.Length != 1) throw new InvalidDataException("安装器未生成唯一的客户端配置。");
                profile = JsonNode.Parse(await File.ReadAllTextAsync(profiles[0], ct).ConfigureAwait(false)) as JsonObject ?? throw new InvalidDataException("安装器配置无效。");
            }
            if (profile["inheritsFrom"] is not null && (string?)profile["inheritsFrom"] != game) throw new InvalidDataException("安装器输出父版本不匹配。");
            if (profile["downloads"]?["client"]?["sha1"] is { } actual && (string?)actual != (string?)vanilla.Metadata["downloads"]?["client"]?["sha1"]) throw new InvalidDataException("安装器客户端与所选基础版本不一致。");
            profile["id"] = id; profile["inheritsFrom"] = game;
            if (loader == "labymod") profile["javaVersion"] = new JsonObject { ["majorVersion"] = java.MajorVersion, ["component"] = vanilla.JavaComponent };
            ValidateProfile(loader, profile); _ = VersionMetadataResolver.ParseVersion(profile.ToJsonString(), id);
            if (loader == "labymod")
            {
                foreach (var node in profile["libraries"] as JsonArray ?? [])
                {
                    if (node is not JsonObject lib || (string?)lib["name"] != "net.labymod:LabyMod:4" || lib["downloads"]?["artifact"] is not JsonObject artifact) continue;
                    var old = ContentInstallFiles.Literal(stage, "libraries/" + (string)artifact["path"]!);
                    var coordinate = "net.labymod:LabyMod:4-" + row.ArtifactVersion; var path = LibraryResolver.MavenPath(coordinate);
                    var target = ContentInstallFiles.Literal(stage, "libraries/" + path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(old, target, false);
                    lib["name"] = coordinate; artifact["path"] = path;
                }
            }
            Directory.CreateDirectory(Path.Combine(stage, "libraries"));
            var libraries = await Files.ImportLibrariesAsync(stage, ct).ConfigureAwait(false);
            foreach (var lib in profile["libraries"] as JsonArray ?? [])
            {
                if (lib is JsonObject descriptor && descriptor["downloads"] is null && (string?)descriptor["name"] is { } coordinate)
                {
                    var produced = LibraryResolver.MavenPath(coordinate);
                    if (libraries.ContainsKey(produced)) descriptor["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = produced, ["url"] = "" } };
                }
                if (lib?["downloads"]?["artifact"] is not JsonObject artifact || (string?)artifact["path"] is not { } path || !string.IsNullOrEmpty((string?)artifact["url"])) continue;
                var file = ContentInstallFiles.Literal(cache, "libraries/" + path);
                if (!libraries.ContainsKey(path)) throw new InvalidDataException("安装器未生成所需运行库。");
                artifact["url"] = "https://libraries.minecraft.net/" + path; artifact["shouGenerated"] = true;
                artifact["sha1"] = await DigestAsync(file, false, ct); artifact["size"] = new FileInfo(file).Length;
            }
            var payload = new Dictionary<string, string>();
            var payloadSource = Path.Combine(stage, "labymod-neo");
            if (Directory.Exists(payloadSource))
            {
                foreach (var source in Directory.GetFiles(payloadSource, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(stage, source).Replace('\\', '/');
                    _ = ContentInstallFiles.Literal(stage, relative);
                    var target = ContentInstallFiles.Literal(root, "payload/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Copy(source, target);
                    var hash = await DigestAsync(source, true, ct); if (!await OfficialLoaderInstaller.HashMatchesAsync(target, hash, true, ct)) throw new IOException("已缓存的 LabyMod 文件发生变化。");
                    payload.Add(relative, hash);
                }
            }
            await OfficialLoaderInstaller.AtomicJsonAsync(Profile(id), profile, ct).ConfigureAwait(false);
            await OfficialLoaderInstaller.AtomicJsonAsync(recipe, new VendorInstallReceipt(loader, game, version, row.ArtifactVersion, installerHash, await DigestAsync(Profile(id), true, ct), libraries, payload), ct).ConfigureAwait(false);
            return id;
        }
        finally { Downloads.VerifiedDownloader.RejectLink(stage); if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    internal async Task CopyPayloadAsync(GameInstance instance, CancellationToken ct)
    {
        var root = Root(instance.Loader, Instances.InstanceIdentity.GameVersion(instance), instance.LoaderVersion!);
        var receipt = JsonSerializer.Deserialize<VendorInstallReceipt>(await File.ReadAllTextAsync(ContentInstallFiles.Literal(root, "installed.json"), ct), Configuration.ConfigurationStore.JsonOptions) ?? throw new InvalidDataException("安装收据缺失。");
        foreach (var (path, hash) in receipt.Payload)
        {
            ct.ThrowIfCancellationRequested(); var source = ContentInstallFiles.Literal(root, "payload/" + path); var target = ContentInstallFiles.Literal(instance.GameDirectory, path);
            if (!path.StartsWith("labymod-neo/", StringComparison.Ordinal) || !await OfficialLoaderInstaller.HashMatchesAsync(source, hash, true, ct)) throw new InvalidDataException("安装器附属文件无效。");
            if (File.Exists(target)) { if (!await OfficialLoaderInstaller.HashMatchesAsync(target, hash, true, ct)) throw new IOException("实例内已有不同的 LabyMod 文件，未覆盖。"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
    }
    internal static void ValidateProfile(string loader, JsonObject profile)
    {
        var main = (string?)profile["mainClass"] ?? ""; var text = profile.ToJsonString();
        var valid = loader switch { "optifine" => main == "net.minecraft.launchwrapper.Launch" && text.Contains("optifine.OptiFineTweaker", StringComparison.Ordinal),
            "liteloader" => main == "net.minecraft.launchwrapper.Launch" && text.Contains("com.mumfrey.liteloader.launch.LiteLoaderTweaker", StringComparison.Ordinal),
            "labymod" => main == "net.minecraft.launchwrapper.Launch" && profile["labymod_data"] is JsonObject
                && (profile["libraries"] as JsonArray ?? []).Any(l => ((string?)l?["name"])?.StartsWith("net.labymod:LabyMod:", StringComparison.Ordinal) == true), _ => false };
        if (!valid) throw new InvalidDataException("安装器主类与加载器身份不匹配。");
    }
    private static async Task<JsonObject> ExtractLiteAsync(string installer, string game, string stage, CancellationToken ct)
    {
        using var jar = ZipFile.OpenRead(installer);
        var entry = jar.GetEntry("install_profile.json") ?? throw new InvalidDataException("LiteLoader 安装元数据缺失。");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("LiteLoader 元数据过大。");
        using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync(ct);
        var json = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject ?? throw new InvalidDataException("LiteLoader 元数据无效。");
        if ((string?)json["install"]?["minecraft"] != game || (string?)json["versionInfo"]?["inheritsFrom"] != game) throw new InvalidDataException("LiteLoader Minecraft 身份不匹配。");
        var profile = (JsonObject)json["versionInfo"]!.DeepClone(); var coordinate = (string?)json["install"]?["path"] ?? throw new InvalidDataException("LiteLoader 库身份缺失。");
        if (coordinate != "com.mumfrey:liteloader:" + game) throw new InvalidDataException("LiteLoader 库坐标不匹配。");
        var embedded = (string?)json["install"]?["filePath"] ?? ""; ModrinthPackArchive.SafeRelative(embedded);
        var binary = jar.GetEntry(embedded) ?? throw new InvalidDataException("LiteLoader 安装器未包含运行库。");
        if (binary.Length is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("LiteLoader 运行库大小无效。");
        var path = LibraryResolver.MavenPath(coordinate); var destination = ContentInstallFiles.Literal(stage, "libraries/" + path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = binary.Open()) await using (var output = new FileStream(destination, FileMode.CreateNew)) await input.CopyToAsync(output, ct);
        var lib = (profile["libraries"] as JsonArray ?? []).Single(n => (string?)n?["name"] == coordinate) as JsonObject ?? throw new InvalidDataException("LiteLoader 配置未引用所需库。");
        lib["url"] = null; lib["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = path, ["url"] = "" } };
        return profile;
    }
    private async Task<bool> ValidAsync(string receiptPath, string installer, string profile, string loader, string game, string version, CancellationToken ct)
    {
        if (!File.Exists(receiptPath)) return false;
        try
        {
            Downloads.VerifiedDownloader.RejectLink(receiptPath);
            if (new FileInfo(receiptPath).Length > 4 * 1024 * 1024) return false;
            var receipt = JsonSerializer.Deserialize<VendorInstallReceipt>(await File.ReadAllTextAsync(receiptPath, ct), Configuration.ConfigurationStore.JsonOptions);
            if (receipt is null || receipt.Loader != loader || receipt.Game != game || receipt.Version != version || receipt.Libraries is null || receipt.Payload is null
                || !await OfficialLoaderInstaller.HashMatchesAsync(installer, receipt.InstallerHash, true, ct) || !await OfficialLoaderInstaller.HashMatchesAsync(profile, receipt.ProfileHash, true, ct)) return false;
            foreach (var (path, hash) in receipt.Libraries) if (!await OfficialLoaderInstaller.HashMatchesAsync(ContentInstallFiles.Literal(cache, "libraries/" + path), hash, true, ct)) return false;
            foreach (var (path, hash) in receipt.Payload) if (!await OfficialLoaderInstaller.HashMatchesAsync(ContentInstallFiles.Literal(Path.GetDirectoryName(receiptPath)!, "payload/" + path), hash, true, ct)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return false; }
    }
    private static async Task<string> DigestAsync(string path, bool sha256, CancellationToken ct)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(sha256 ? await SHA256.HashDataAsync(file, ct) : await SHA1.HashDataAsync(file, ct)); }
}
