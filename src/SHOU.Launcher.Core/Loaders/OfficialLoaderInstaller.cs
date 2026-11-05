using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Loaders;

public sealed record LoaderRecipe(string Loader, string GameVersion, string LoaderVersion, string ProfileId, string InstallerRelativePath, string InstallerSha1);
public sealed record LoaderInstallReceipt(string InstallerSha1, string ProfileSha256, IReadOnlyDictionary<string, string> Libraries);

/// <summary>Runs the hash-verified vendor client installer in a private staging root. Never opens a vendor GUI or a user's official-launcher profile.</summary>
public sealed class OfficialLoaderInstaller(HttpClient http, IVerifiedDownloader downloader, IProcessRunner processes,
    string cache, string temporaryRoot, Func<IReadOnlyList<DownloadSource>> sources, Func<ProxySettings> proxySettings)
{
    private const long MaximumStagedBytes = 2L * 1024 * 1024 * 1024;
    private LoaderCatalog Catalog => new(http);
    public async Task<LoaderRecipe> CacheAsync(string loader, string gameVersion, string loaderVersion, CancellationToken ct)
    {
        SafePath.Identifier(gameVersion); SafePath.Identifier(loaderVersion);
        if (loader is not ("forge" or "neoforge")) throw new ArgumentException("Unsupported official installer.");
        if (loader == "neoforge" && !loaderVersion.StartsWith(LoaderCatalog.NeoPrefix(gameVersion), StringComparison.Ordinal)) throw new InvalidDataException("NeoForge与Minecraft版本不匹配。");
        var artifactVersion = loader == "forge" ? gameVersion + "-" + loaderVersion : loaderVersion;
        var stem = loader + "-" + artifactVersion + "-installer.jar";
        var uri = new Uri(loader == "forge" ? $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifactVersion}/{stem}" : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{artifactVersion}/{stem}");
        var sha = (await Catalog.TextAsync(new(uri.AbsoluteUri + ".sha1"), 4096, ct)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha?.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("官方安装器校验值无效。");
        var relative = $"loaders/{loader}/{artifactVersion}/{stem}"; var installer = ContentInstallFiles.Literal(cache, relative);
        using var sizeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); sizeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var sizeResponse = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, sizeTimeout.Token).ConfigureAwait(false);
        sizeResponse.EnsureSuccessStatusCode();
        var length = sizeResponse.Content.Headers.ContentLength;
        if (length is not (> 0 and <= 32 * 1024 * 1024)) throw new InvalidDataException("官方安装器未提供有效的32MiB以内大小。");
        await downloader.DownloadAsync(new(Map(uri), installer, sha, Size: length, AllowResume: true), cancellationToken: ct).ConfigureAwait(false);
        if (new FileInfo(installer).Length > 32 * 1024 * 1024) throw new InvalidDataException("官方安装器超过32MiB限制。");
        using var jar = ZipFile.OpenRead(installer);
        var install = await JsonEntryAsync(jar, "install_profile.json", ct); var version = await JsonEntryAsync(jar, "version.json", ct);
        if ((string?)install["minecraft"] != gameVersion || (string?)version["inheritsFrom"] != gameVersion) throw new InvalidDataException("官方安装器中的Minecraft版本不匹配。");
        var id = SafePath.Identifier((string?)version["id"] ?? throw new InvalidDataException("安装器没有版本标识。"));
        if (id != (loader == "forge" ? gameVersion + "-forge-" + loaderVersion : "neoforge-" + loaderVersion)) throw new InvalidDataException("安装器加载器版本标识不匹配。");
        ValidateMainClass(version);
        _ = VersionMetadataResolver.ParseVersion(version.ToJsonString(), id);
        var recipe = new LoaderRecipe(loader, gameVersion, loaderVersion, id, relative, sha.ToLowerInvariant());
        var path = Profile(id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Existing completed manifests stay intact until a successful replacement is ready.
        if (!File.Exists(path)) await AtomicJsonAsync(path, version, ct);
        await AtomicJsonAsync(RecipePath(id), recipe, ct);
        return recipe;
    }
    public async Task EnsureInstalledAsync(GameInstance instance, ResolvedVersion vanilla, JavaInstallation java, RuleContext context,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var recipeFile = RecipePath(instance.VersionId);
        var recipe = File.Exists(recipeFile) ? JsonSerializer.Deserialize<LoaderRecipe>(await ReadJsonTextAsync(recipeFile, 64 * 1024, ct), ConfigurationStore.JsonOptions) : null;
        if (recipe is null || recipe.Loader != instance.Loader || recipe.LoaderVersion != instance.LoaderVersion || recipe.GameVersion != vanilla.Id || recipe.ProfileId != instance.VersionId)
            throw new InvalidDataException("加载器安装记录缺失或不匹配，请重新创建实例。");
        var installer = ContentInstallFiles.Literal(cache, recipe.InstallerRelativePath);
        if (!await HashMatchesAsync(installer, recipe.InstallerSha1, false, ct)) throw new InvalidDataException("加载器安装器缓存已变化，请重新创建或下载。");
        if (await ReceiptValidAsync(recipe, ct)) return;
        var proxy = proxySettings();
        if (proxy is { Mode: ProxyMode.Custom, CredentialKey: { Length: > 0 } }) throw new PlatformNotSupportedException("官方Java处理器暂不支持需要凭据的HTTP代理；请使用系统代理或无认证代理。");
        var stage = ContentInstallFiles.Literal(temporaryRoot, "loader-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var id = "loader-processor-" + instance.Loader + "-" + instance.LoaderVersion;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stage, "launcher_profiles.json"), "{\"profiles\":{}}", ct);
            var versionFolder = SafePath.Within(stage, "versions/" + vanilla.Id); Directory.CreateDirectory(versionFolder);
            await AtomicJsonAsync(Path.Combine(versionFolder, vanilla.Id + ".json"), vanilla.Metadata, ct);
            var client = vanilla.Metadata["downloads"]?["client"] as JsonObject ?? throw new InvalidDataException("缺少原版客户端。");
            var cachedClient = ContentInstallFiles.Literal(cache, "versions/" + vanilla.Id + "/" + vanilla.Id + ".jar");
            await FetchAsync(client, cachedClient, progress, ct);
            File.Copy(cachedClient, Path.Combine(versionFolder, vanilla.Id + ".jar"));
            using (var jar = ZipFile.OpenRead(installer))
            {
                var profile = await JsonEntryAsync(jar, "install_profile.json", ct); var runtime = await JsonEntryAsync(jar, "version.json", ct);
                var entries = (profile["libraries"] as JsonArray ?? []).Concat(runtime["libraries"] as JsonArray ?? []).Concat(vanilla.Metadata["libraries"] as JsonArray ?? []);
                var seeded = new HashSet<string>(StringComparer.Ordinal); long bytes = new FileInfo(cachedClient).Length;
                foreach (var node in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (node?["downloads"]?["artifact"] is not JsonObject artifact || (string?)artifact["path"] is not { } relative || !seeded.Add(relative)) continue;
                    var source = ContentInstallFiles.Literal(cache, "libraries/" + relative);
                    if (!File.Exists(source) || !await HashMatchesAsync(source, (string?)artifact["sha1"], false, ct)) continue;
                    RejectLink(source); bytes += new FileInfo(source).Length; if (bytes > MaximumStagedBytes) throw new InvalidDataException("加载器暂存超过2GiB限制。");
                    var destination = SafePath.Within(stage, "libraries/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
                }
            }
            var home = Path.Combine(stage, "home"); var tmp = Path.Combine(stage, "tmp"); Directory.CreateDirectory(home); Directory.CreateDirectory(tmp);
            var args = new List<string> { "-Djava.awt.headless=true", "-Duser.home=" + home, "-Djava.io.tmpdir=" + tmp };
            if (proxy.Mode == ProxyMode.System) args.Add("-Djava.net.useSystemProxies=true");
            if (proxy.Mode == ProxyMode.Custom)
            {
                var address = new Uri(proxy.Address!);
                args.AddRange(["-Dhttp.proxyHost=" + address.Host, "-Dhttp.proxyPort=" + address.Port, "-Dhttps.proxyHost=" + address.Host, "-Dhttps.proxyPort=" + address.Port]);
            }
            args.AddRange(["-jar", installer, "--installClient", stage]);
            var environment = new Dictionary<string, string?> { ["JAVA_HOME"] = java.JavaHome, ["JAVA_TOOL_OPTIONS"] = null, ["JDK_JAVA_OPTIONS"] = null, ["_JAVA_OPTIONS"] = null, ["CLASSPATH"] = null };
            foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("PCL_", StringComparison.Ordinal) || k is "MS_CLIENT_ID" or "CLIENT_ID" or "CURSEFORGE_API" or "CURSEFORGE_API_KEY")) environment[key] = null;
            progress?.Report(new(id, "Downloading", 0, null));
            var result = await RunWithStageBudgetAsync(new(java.ExecutablePath, args, stage, environment, TimeSpan.FromMinutes(8), 1024 * 1024), stage, ct).ConfigureAwait(false);
            if (result.Cancelled) throw new OperationCanceledException(ct);
            if (result.TimedOut || result.ExitCode != 0) throw new IOException($"官方{instance.Loader}处理器未完成（exit={result.ExitCode}，timeout={result.TimedOut}）。" + Tail(result.StandardError + "\n" + result.StandardOutput));
            var generatedPath = ContentInstallFiles.Literal(stage, "versions/" + recipe.ProfileId + "/" + recipe.ProfileId + ".json");
            var generated = VersionMetadataResolver.ParseVersion(await ReadJsonTextAsync(generatedPath, 4 * 1024 * 1024, ct), recipe.ProfileId);
            if ((string?)generated["inheritsFrom"] != vanilla.Id) throw new InvalidDataException("处理器输出的父版本不匹配。");
            ValidateMainClass(generated);
            var hashes = await ImportLibrariesAsync(stage, ct);
            foreach (var library in generated["libraries"] as JsonArray ?? [])
            {
                if (library?["downloads"]?["artifact"] is not JsonObject artifact || (string?)artifact["path"] is not { } relative) continue;
                if (string.IsNullOrEmpty((string?)artifact["url"]))
                {
                    var file = ContentInstallFiles.Literal(cache, "libraries/" + relative);
                    if (!hashes.ContainsKey(relative)) throw new InvalidDataException("处理器未生成所需运行库。");
                    artifact["url"] = "https://libraries.minecraft.net/" + relative; artifact["shouGenerated"] = true;
                    artifact["sha1"] = await DigestAsync(file, false, ct); artifact["size"] = new FileInfo(file).Length;
                }
            }
            await AtomicJsonAsync(Profile(recipe.ProfileId), generated, ct);
            var receipt = new LoaderInstallReceipt(recipe.InstallerSha1, await DigestAsync(Profile(recipe.ProfileId), true, ct), hashes);
            await AtomicJsonAsync(ReceiptPath(recipe.ProfileId), receipt, ct);
            progress?.Report(new(id, "Completed", 1, 1));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private async Task<Dictionary<string, string>> ImportLibrariesAsync(string stage, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal); var root = Path.Combine(stage, "libraries"); long bytes = 0;
        var folders = new Stack<(string Path, int Depth)>(); folders.Push((root, 0));
        while (folders.TryPop(out var folder))
        {
            if (folder.Depth > 24) throw new InvalidDataException("加载器目录过深。"); RejectLink(folder.Path);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
            {
                ct.ThrowIfCancellationRequested(); RejectLink(path);
                if (Directory.Exists(path)) { folders.Push((path, folder.Depth + 1)); continue; }
                bytes += new FileInfo(path).Length; if (bytes > MaximumStagedBytes || result.Count >= 10000) throw new InvalidDataException("加载器输出超过限制。");
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/'); var target = ContentInstallFiles.Literal(cache, "libraries/" + relative); var hash = await DigestAsync(path, true, ct);
                if (File.Exists(target)) { RejectLink(target); if (!await HashMatchesAsync(target, hash, true, ct)) throw new IOException("共享运行库已有不同内容，未覆盖：" + relative); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
                    try { File.Copy(path, temporary); ct.ThrowIfCancellationRequested(); File.Move(temporary, target, false); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                result[relative] = hash;
            }
        }
        return result;
    }
    private async Task<bool> ReceiptValidAsync(LoaderRecipe recipe, CancellationToken ct)
    {
        try
        {
            var file = ReceiptPath(recipe.ProfileId); if (!File.Exists(file)) return false;
            var receipt = JsonSerializer.Deserialize<LoaderInstallReceipt>(await ReadJsonTextAsync(file, 4 * 1024 * 1024, ct), ConfigurationStore.JsonOptions);
            if (receipt is null || receipt.InstallerSha1 != recipe.InstallerSha1 || !await HashMatchesAsync(Profile(recipe.ProfileId), receipt.ProfileSha256, true, ct)) return false;
            foreach (var pair in receipt.Libraries) if (!await HashMatchesAsync(ContentInstallFiles.Literal(cache, "libraries/" + pair.Key), pair.Value, true, ct)) return false;
            return receipt.Libraries.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
    private Task FetchAsync(JsonObject descriptor, string path, IProgress<TransferProgress>? progress, CancellationToken ct) => downloader.DownloadAsync(new(Map(new((string?)descriptor["url"] ?? throw new InvalidDataException("缺少下载URL。"))), path,
        (string?)descriptor["sha1"] ?? throw new InvalidDataException("缺少下载校验。"), Size: (long?)descriptor["size"]), progress, ct);
    private IReadOnlyList<Uri> Map(Uri uri) => DownloadSources.Resolve([uri.AbsoluteUri], sources()).Select(s => new Uri(s.Url)).ToArray();
    private string Profile(string id) => ContentInstallFiles.Literal(cache, "versions/" + SafePath.Identifier(id) + "/" + id + ".json");
    private string RecipePath(string id) => ContentInstallFiles.Literal(cache, "versions/" + SafePath.Identifier(id) + "/loader-recipe.json");
    private string ReceiptPath(string id) => ContentInstallFiles.Literal(cache, "versions/" + SafePath.Identifier(id) + "/loader-installed.json");
    private static void ValidateMainClass(JsonObject profile)
    {
        if ((string?)profile["mainClass"] != "cpw.mods.bootstraplauncher.BootstrapLauncher") throw new InvalidDataException("此官方安装器的启动主类尚未支持，未记入已安装缓存。");
    }
    private async Task<ProcessResult> RunWithStageBudgetAsync(ProcessSpec spec, string stage, CancellationToken ct)
    {
        CheckStageBudget(stage, ct);
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = processes.RunAsync(spec, cancellationToken: owned.Token);
        try
        {
            while (!running.IsCompleted)
            {
                if (await Task.WhenAny(running, Task.Delay(500, ct)).ConfigureAwait(false) == running) break;
                ct.ThrowIfCancellationRequested(); CheckStageBudget(stage, ct);
            }
            var result = await running.ConfigureAwait(false); CheckStageBudget(stage, ct); return result;
        }
        catch
        {
            owned.Cancel();
            try { await running.ConfigureAwait(false); } catch (Exception) { /* Original budget/cancellation failure is authoritative. */ }
            throw;
        }
    }
    private static void CheckStageBudget(string stage, CancellationToken ct)
    {
        long bytes = 0; var entries = 0; var pending = new Stack<(string Path, int Depth)>(); pending.Push((stage, 0));
        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                RejectLink(folder.Path);
                if (folder.Depth > 24) throw new InvalidDataException("加载器暂存目录超过24层限制。");
                foreach (var path in Directory.EnumerateFileSystemEntries(folder.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        RejectLink(path);
                        if (++entries > 10000) throw new InvalidDataException("加载器暂存超过10000项限制。");
                        if (Directory.Exists(path)) pending.Push((path, folder.Depth + 1));
                        else if ((bytes += new FileInfo(path).Length) > MaximumStagedBytes) throw new InvalidDataException("加载器整体暂存超过2GiB限制，处理器已停止。");
                    }
                    catch (FileNotFoundException) { /* The active vendor process may rename a temporary file. */ }
                    catch (DirectoryNotFoundException) { }
                }
            }
            catch (FileNotFoundException) when (folder.Depth > 0) { }
            catch (DirectoryNotFoundException) when (folder.Depth > 0) { }
        }
    }
    private static void RejectLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); info.Refresh();
        if (!info.Exists && info.LinkTarget is null) throw new FileNotFoundException("加载器文件已移动。", path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("加载器缓存不接受链接。");
    }
    private static async Task<bool> HashMatchesAsync(string file, string? expected, bool sha256, CancellationToken ct)
    { if (expected?.Length != (sha256 ? 64 : 40) || !expected.All(Uri.IsHexDigit) || !File.Exists(file)) return false; RejectLink(file); return string.Equals(await DigestAsync(file, sha256, ct), expected, StringComparison.OrdinalIgnoreCase); }
    private static async Task<string> DigestAsync(string path, bool sha256, CancellationToken ct)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); return Convert.ToHexStringLower(sha256 ? await SHA256.HashDataAsync(file, ct) : await SHA1.HashDataAsync(file, ct)); }
    private static async Task<JsonObject> JsonEntryAsync(ZipArchive jar, string name, CancellationToken ct)
    {
        var entries = jar.Entries.Where(e => e.FullName == name).ToArray(); if (entries.Length != 1 || entries[0].Length > 4 * 1024 * 1024) throw new InvalidDataException("安装器元数据缺失、重复或过大。");
        await using var input = entries[0].Open(); using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var count = await input.ReadAsync(buffer, ct); if (count == 0) break; if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("安装器元数据解压超过限制。"); output.Write(buffer, 0, count); }
        return JsonNode.Parse(output.ToArray()) as JsonObject ?? throw new InvalidDataException("安装器元数据格式无效。");
    }
    private static async Task<string> ReadJsonTextAsync(string file, int maximum, CancellationToken ct)
    {
        RejectLink(file); if (new FileInfo(file).Length > maximum) throw new InvalidDataException("加载器本地元数据超过限制。");
        return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
    }
    private static async Task AtomicJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Downloads.VerifiedDownloader.RejectLink(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var stage = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(stage, JsonSerializer.Serialize(value, ConfigurationStore.JsonOptions), ct); Downloads.VerifiedDownloader.RejectLink(path); File.Move(stage, path, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private static string Tail(string text) => text.Length > 1800 ? text[^1800..] : text;
}
