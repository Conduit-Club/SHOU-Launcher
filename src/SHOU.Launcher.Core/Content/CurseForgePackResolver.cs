using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

public sealed record CurseForgePackReference(int ProjectId, int FileId, bool Required);
public sealed record CurseForgePackFile(int ProjectId, int FileId, string Path, string Sha1, string? Sha512,
    long Size, Uri DownloadUri, bool FromModrinth, bool FromFtb = false, bool FromManual = false);
public sealed record CurseForgePackResolution(IReadOnlyList<CurseForgePackFile> Files,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> BlockedFiles, IReadOnlyList<CurseForgeManualFile>? ManualFiles = null);

/// <summary>Caller-owned dedicated HttpClient MUST disable automatic redirects. API key is attached only to fixed CF requests.</summary>
public sealed class CurseForgePackResolver
{
    private readonly HttpClient http;
    private readonly string? apiKey;
    private readonly string? manualCacheRoot;
    private readonly NetworkOptions? network;
    public CurseForgePackResolver(HttpClient http, string? apiKey, string? manualCacheRoot = null, NetworkOptions? network = null)
    {
        this.http = http;
        this.manualCacheRoot = manualCacheRoot;
        this.network = network;
        if (http.DefaultRequestHeaders.Contains("x-api-key") || http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie")) throw new ArgumentException("整合包专用客户端不能携带全局鉴权头。");
        if (apiKey is not null && (apiKey.Length > 8192 || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))) throw new ArgumentException("CurseForge API key 配置格式无效。");
        this.apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
    }
    public async Task<CurseForgePackResolution> ResolveAsync(IReadOnlyList<CurseForgePackReference> references, string gameVersion, string loader, CancellationToken ct = default,
        FtbPackIdentity? ftbPack = null, IProgress<PackInstallProgress>? progress = null)
    {
        if (references.Count > ModrinthPackArchive.MaximumFiles || references.Any(r => r.ProjectId <= 0 || r.FileId <= 0)) throw new InvalidDataException("CurseForge 清单项目数量或身份无效。");
        var required = references.Where(r => r.Required).ToArray();
        if (apiKey is null) return new([], [], required.Select(r => Label(r) + "：未配置 CurseForge API key，尚未核实文件。").ToArray());
        var publishedFtb = await FtbPackFileIndex.ReadAsync(http, ftbPack, ct).ConfigureAwait(false);
        var completed = 0;
        progress?.Report(new("解析必需文件", 0, required.Length));
        var results = new Item?[required.Length]; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(180, required.Length * 2L)));
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, required.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (index, token) =>
            {
                results[index] = await ResolveOneAsync(required[index], gameVersion, loader, references, publishedFtb, token).ConfigureAwait(false);
                progress?.Report(new("解析必需文件", Interlocked.Increment(ref completed), required.Length, Label(required[index])));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        var files = new List<CurseForgePackFile>(); var warnings = new List<string>(); var blocked = new List<string>();
        for (var i = 0; i < required.Length; i++)
        {
            if (results[i] is not { } item) { blocked.Add(Label(required[i]) + "：官方文件解析超时，尚未核实。"); continue; }
            warnings.AddRange(item.Warnings); if (item.File is { } file) files.Add(file); else blocked.Add(Label(required[i]) + "：" + item.Reason);
        }
        if (references.Any(r => !r.Required)) warnings.Add("CurseForge 可选文件本轮默认不安装，仅安装清单内 required 文件。");
        if (files.Sum(f => f.Size) > 4L * 1024 * 1024 * 1024) blocked.Add("清单下载总量超过 4 GiB，本次不会创建实例。");
        if (files.Select(f => ContentInstallFiles.PathKey(f.Path)).Distinct(StringComparer.Ordinal).Count() != files.Count) blocked.Add("不同 CurseForge 项目映射到相同文件路径，本次不会创建实例。");
        return new(files, warnings, blocked, results.Where(r => r?.Manual is not null).Select(r => r!.Manual!).ToArray());
    }
    private sealed record Item(CurseForgePackFile? File, IReadOnlyList<string> Warnings, string? Reason = null, CurseForgeManualFile? Manual = null);
    internal async Task<IReadOnlyList<LocalPackOptionalFile>> DescribeOptionalFilesAsync(IReadOnlyList<CurseForgePackReference> references, CancellationToken ct)
    {
        var files = references.Where(f => !f.Required).ToArray();
        var choices = files.Select(f => CurseForgePackSelection.Choice(f, notice: "官方名称尚未核实；选中后会核实文件身份与可用性。")).ToArray();
        if (apiKey is null) return choices;
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var f = files[index];
            try
            {
                using var response = await GetJsonAsync(new Uri($"https://api.curseforge.com/v1/mods/{f.ProjectId}/files/{f.FileId}"), true, token).ConfigureAwait(false);
                var data = response.RootElement.GetProperty("data");
                if (Number(data, "id") != f.FileId || Number(data, "modId") != f.ProjectId) return;
                var name = Text(data, "fileName", 200);
                choices[index] = CurseForgePackSelection.Choice(f, name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is LookupException or HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
            { /* An unselected optional file cannot block required files; selecting it runs full validation. */ }
        }).ConfigureAwait(false);
        return choices;
    }
    private async Task<Item> ResolveOneAsync(CurseForgePackReference reference, string gameVersion, string loader, IReadOnlyList<CurseForgePackReference> all,
        IReadOnlyDictionary<(int, int), FtbPublishedFile> publishedFtb, CancellationToken ct)
    {
        CurseForgeManualFile? manual = null;
        try
        {
            using var response = await GetJsonAsync(new Uri($"https://api.curseforge.com/v1/mods/{reference.ProjectId}/files/{reference.FileId}"), true, ct).ConfigureAwait(false);
            var data = response.RootElement.GetProperty("data");
            if (Number(data, "id") != reference.FileId || Number(data, "modId") != reference.ProjectId || Number(data, "gameId") != 432) throw new InvalidDataException("官方文件身份或游戏不匹配。");
            var available = data.GetProperty("isAvailable").GetBoolean();
            int? status = data.TryGetProperty("fileStatus", out var statusValue) && statusValue.ValueKind != JsonValueKind.Null ? statusValue.GetInt32() : null;
            if (status is 5 or 6 or 7) throw new InvalidDataException(status switch
            { 5 => "官方文件审核已拒绝（fileStatus=5），不能导入。", 6 => "官方文件被标记为恶意软件（fileStatus=6），不能导入。", _ => "官方文件已删除（fileStatus=7），不能导入。" });
            // Official support permits archived files only as pack/mod dependencies, not standalone downloads.
            var archivedDependency = !available && status == 8 && reference.Required;
            if (!available && !archivedDependency) throw new InvalidDataException(status switch
            {
                null or < 1 or > 23 => "官方文件状态不明且当前不可供下载，不能套用归档依赖例外。",
                3 or 11 or 18 => $"官方文件仍在审核中（fileStatus={status}），当前不可供下载。",
                _ => $"官方文件当前不可供下载（fileStatus={status}），不适用归档依赖例外。"
            });
            if (data.TryGetProperty("isServerPack", out var server) && server.ValueKind == JsonValueKind.True) throw new InvalidDataException("此文件是服务器整合包，不能作为客户端模组导入。");
            var name = Text(data, "fileName", 200); ModrinthPackArchive.SafeRelative(name);
            if (name.Contains('/')) throw new InvalidDataException("官方文件名不能包含目录路径。");
            using var projectResponse = await GetJsonAsync(new Uri($"https://api.curseforge.com/v1/mods/{reference.ProjectId}"), true, ct).ConfigureAwait(false);
            var project = projectResponse.RootElement.GetProperty("data");
            if (Number(project, "id") != reference.ProjectId || Number(project, "gameId") != 432) throw new InvalidDataException("官方项目身份或游戏不匹配。");
            var classId = Number(project, "classId");
            var folder = classId == 6 && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods/"
                : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? classId switch
                {
                    12 => "resourcepacks/", 6552 => "shaderpacks/", 6945 => "datapacks/", 17 => "saves/",
                    _ => throw new InvalidDataException("CurseForge 文件的项目类型尚未映射到实例目录。")
                } : throw new InvalidDataException("CurseForge 文件扩展名与项目类型不匹配。");
            var size = Number(data, "fileLength"); if (size is <= 0 or > PackArchiveReader.MaximumFileBytes) throw new InvalidDataException("官方文件大小超过单项 128 MiB 边界。");
            var hashes = data.GetProperty("hashes");
            if (hashes.ValueKind != JsonValueKind.Array || hashes.GetArrayLength() > 8) throw new InvalidDataException("官方哈希列表无效。");
            var sha1s = hashes.EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => Text(h, "value", 40)).ToArray();
            if (sha1s.Length != 1 || !ContentInstallFiles.Hash(sha1s[0], 40)) throw new InvalidDataException("官方文件缺少唯一有效 SHA-1。");
            var sha1 = sha1s[0].ToLowerInvariant(); var warnings = new List<string>();
            if (classId == 6945) warnings.Add(Label(reference) + "：整合包数据包装入实例 datapacks；全局装载由整合包模组负责，不改写已有世界的启停设置。");
            var versions = data.TryGetProperty("gameVersions", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Take(128).Select(v => v.GetString() ?? "").ToArray() : [];
            if (!versions.Contains(gameVersion, StringComparer.Ordinal)) warnings.Add(Label(reference) + "：官方标签未列出目标 " + gameVersion + "，实际兼容性仍由加载器验证。");
            if (classId == 6 && loader == "fabric" && !versions.Contains("Fabric", StringComparer.OrdinalIgnoreCase)) warnings.Add(Label(reference) + "：官方标签未声明 Fabric，需由加载器最终验证。");
            if (data.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array)
            {
                if (dependencies.GetArrayLength() > 256) throw new InvalidDataException("官方文件依赖数量超过边界。");
                foreach (var dependency in dependencies.EnumerateArray()) if (Number(dependency, "relationType") == 3 && !all.Any(r => r.Required && r.ProjectId == Number(dependency, "modId")))
                    warnings.Add(Label(reference) + "：声明的依赖项目 " + Number(dependency, "modId") + " 未列入必装清单，本轮不会猜测版本补装。");
            }
            if (data.TryGetProperty("downloadUrl", out var direct) && direct.ValueKind == JsonValueKind.String && direct.GetString() is { Length: > 0 } address)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !AllowedCurseForgeUri(uri)) throw new InvalidDataException("官方文件返回了不受支持的下载来源。");
                if (archivedDependency) warnings.Add(Label(reference) + "：已归档的历史包依赖，仅按清单锁定版本使用官方提供的 CDN 地址，并核验 SHA-1 与大小；不会作为独立文件安装。");
                return new(new(reference.ProjectId, reference.FileId, folder + name, sha1, null, size, uri, false), warnings);
            }
            if (archivedDependency) return new(null, warnings, "已归档的历史包依赖未提供官方 CDN 地址，不能套用归档依赖例外；未猜测链接或查询哈希回退。");
            var page = project.TryGetProperty("links", out var links) && links.TryGetProperty("websiteUrl", out var website) ? website.GetString() : null;
            var pageUri = Uri.TryCreate(page, UriKind.Absolute, out var projectPage) && projectPage.Scheme == "https" && projectPage.IsDefaultPort && projectPage.UserInfo.Length == 0 && projectPage.Host is "www.curseforge.com" or "curseforge.com"
                ? new Uri(projectPage.AbsoluteUri.TrimEnd('/') + "/files/" + reference.FileId)
                : new Uri($"https://www.curseforge.com/projects/{reference.ProjectId}/files/{reference.FileId}");
            manual = new(reference.ProjectId, reference.FileId, name, sha1, size, pageUri);
            if (manualCacheRoot is not null && await CurseForgeManualCache.DigestAsync(manualCacheRoot, manual, ct).ConfigureAwait(false) is { } localSha512)
                return new(new(reference.ProjectId, reference.FileId, folder + name, sha1, localSha512, size, ManualIdentityUri(reference.ProjectId, reference.FileId), false, FromManual: true), warnings);
            if (publishedFtb.TryGetValue((reference.ProjectId, reference.FileId), out var ftb) && ftb.Folder == folder && ftb.Size == size && ftb.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(Label(reference) + "：采用 FTB 官方同版本清单公布的同项目 / 文件地址，核对 SHA-1、SHA-512 与大小。");
                return new(new(reference.ProjectId, reference.FileId, folder + name, sha1, ftb.Sha512, size, ftb.Uri, false, true), warnings);
            }
            // A null CF URL is not a license to reconstruct its CDN path. Only independently published exact-hash files qualify.
            using var alternative = await GetJsonAsync(new Uri("https://api.modrinth.com/v2/version_file/" + sha1 + "?algorithm=sha1"), false, ct).ConfigureAwait(false);
            var candidates = alternative.RootElement.GetProperty("files");
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 64) throw new InvalidDataException("Modrinth 精确哈希查询返回无效文件列表。");
            foreach (var candidate in candidates.EnumerateArray())
            {
                var digest = candidate.GetProperty("hashes");
                if (!string.Equals(Text(digest, "sha1", 40), sha1, StringComparison.OrdinalIgnoreCase) || Number(candidate, "size") != size) continue;
                var sha512 = Text(digest, "sha512", 128); var uri = new Uri(Text(candidate, "url", 2048));
                if (!ContentInstallFiles.Hash(sha512, 128) || !ContentInstallFiles.AllowedUri(uri)) throw new InvalidDataException("Modrinth 同哈希文件缺少有效 SHA-512 或官方来源。");
                warnings.Add(Label(reference) + "：CurseForge 未提供第三方下载地址；使用 Modrinth 独立发布、同 SHA-1 且同大小的文件，并校验 SHA-512。");
                return new(new(reference.ProjectId, reference.FileId, folder + name, sha1, sha512.ToLowerInvariant(), size, uri, true), warnings);
            }
            return new(null, warnings, "作者未提供第三方下载地址，请从官网获取指定文件并选择已下载文件，校验后继续。", manual);
        }
        catch (OperationCanceledException) { throw; }
        catch (LookupException ex) { return new(null, [], manual is null ? ex.Message : "需从官网获取并补充指定文件：" + manual.FileName, manual); }
        catch (HttpRequestException) { return new(null, [], "官方服务连接失败，请稍后重新预检。"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return new(null, [], ex is InvalidDataException ? ex.Message : "官方服务返回的文件元数据无效，未下载任何文件。"); }
    }
    internal const long MaximumCachedResourceBytes = 2L * 1024 * 1024 * 1024;
    public Task DownloadResourceAsync(ContentFile file, string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => DownloadVerifiedAsync(new(0, 0, file.FileName, file.Sha1, null, file.Size, file.Uri, false), path, MaximumCachedResourceBytes, progress, ct);
    public Task DownloadAsync(CurseForgePackFile file, string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => DownloadVerifiedAsync(file, path, PackArchiveReader.MaximumFileBytes, progress, ct);
    private async Task DownloadVerifiedAsync(CurseForgePackFile file, string path, long maximumBytes, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var label = DownloadLabel(file);
        if (file.FromManual)
        {
            if (manualCacheRoot is null || file.FromFtb || file.FromModrinth || file.DownloadUri != ManualIdentityUri(file.ProjectId, file.FileId)) throw new InvalidDataException("本地补充文件来源无效。");
            var descriptor = new CurseForgeManualFile(file.ProjectId, file.FileId, Path.GetFileName(file.Path), file.Sha1, file.Size, file.DownloadUri);
            var source = CurseForgeManualCache.PathFor(manualCacheRoot, descriptor);
            if (await CurseForgeManualCache.DigestAsync(manualCacheRoot, descriptor, ct).ConfigureAwait(false) is not { } digest || digest != file.Sha512) throw new InvalidDataException("本地补充文件已改变，请重新提供。");
            VerifiedDownloader.RejectLink(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true)) await input.CopyToAsync(output, ct).ConfigureAwait(false);
            if (!await ContentInstallFiles.MatchesAsync(path, file.Sha1, file.Size, ct).ConfigureAwait(false)) throw new InvalidDataException("本地补充文件复制校验失败。");
            await using var copied = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (Convert.ToHexStringLower(await SHA512.HashDataAsync(copied, ct).ConfigureAwait(false)) != digest) throw new InvalidDataException("本地补充文件复制 SHA-512 校验失败。");
            return;
        }
        if (!AllowedDownloadUri(file.DownloadUri, file.FromFtb) || file.FromFtb && (file.FromModrinth || !ContentInstallFiles.Hash(file.Sha512, 128))) throw new InvalidDataException(label + "：文件来源不在官方 CDN 允许范围。");
        if (!ContentInstallFiles.Hash(file.Sha1, 40) || (file.Size <= 0 || file.Size > maximumBytes) || file.Sha512 is not null && !ContentInstallFiles.Hash(file.Sha512, 128))
            throw new InvalidDataException(label + "：文件哈希或大小无效。");
        using var redirects = new CdnRedirectHandler(http, file.FromFtb); using var downloads = new HttpClient(redirects) { Timeout = Timeout.InfiniteTimeSpan };
        // Keep the CDN redirect allowlist and API-key isolation while sharing the global transfer budget.
        var reporting = new CallbackProgress<TransferProgress>(p => progress?.Report(p with { Source = p.Source == "cache" ? "cache" : file.DownloadUri.Host, Backend = p.Backend ?? "内置",
            Detail = p.Backend == "aria2" ? "经官方 CDN 校验通道 · " + p.Detail : p.Detail }));
        try
        {
            var internalDownloader = new VerifiedDownloader(downloads, network);
            if (network?.Aria2.Enabled == true)
            {
                await using var relay = new RestrictedDownloadRelay(downloads, file.DownloadUri, file.Size, network);
                await new DownloadCoordinator(downloads, new CdnFallback(internalDownloader, file.DownloadUri), network, policyHandledByTransport: true).DownloadAsync(new([relay.Uri], path, file.Sha1, Size: file.Size), reporting, ct).ConfigureAwait(false);
            }
            else await internalDownloader.DownloadAsync(new([file.DownloadUri], path, file.Sha1, Size: file.Size), reporting, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AggregateException or HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        { throw new IOException(label + "：下载失败；" + (redirects.FailureReason ?? "文件传输或 SHA-1/大小校验失败。")); }
        if (file.Sha512 is not null)
        {
            string digest;
            try
            {
                VerifiedDownloader.RejectLink(path); await using var bytes = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
                digest = Convert.ToHexStringLower(await SHA512.HashDataAsync(bytes, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { throw new IOException(label + "：无法读取已下载文件进行 SHA-512 校验。"); }
            if (!digest.Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(label + "：下载文件 SHA-512 校验失败。");
        }
    }
    private sealed class CdnFallback(IVerifiedDownloader downloader, Uri source) : IVerifiedDownloader
    {
        public Task DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
            => downloader.DownloadAsync(request with { Sources = [source] }, progress, cancellationToken);
    }
    private static Uri ManualIdentityUri(int project, int file) => new($"https://api.curseforge.com/v1/mods/{project}/files/{file}");
    private static bool AllowedDownloadUri(Uri uri, bool allowFtb = false) => AllowedCurseForgeUri(uri) || ContentInstallFiles.AllowedUri(uri) || allowFtb && FtbPackFileIndex.AllowedUri(uri);
    private static string DownloadLabel(CurseForgePackFile file)
    {
        var name = new string(Path.GetFileName(file.Path).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '+' or '(' or ')').Take(120).ToArray());
        return file.ProjectId > 0 && file.FileId > 0 ? $"项目 {file.ProjectId} / 文件 {file.FileId}（{(name.Length == 0 ? "文件" : name)}）" : $"文件（{(name.Length == 0 ? "资源" : name)}）";
    }
    // This wrapper is used only by file downloads. API requests keep the caller's no-redirect transport directly.
    private sealed class CdnRedirectHandler(HttpClient transport, bool allowFtb) : HttpMessageHandler
    {
        internal string? FailureReason { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is null) throw Failure("文件下载仅接受无请求体的 GET。");
            var uri = request.RequestUri; var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var hops = 0; ; hops++)
            {
                if (!AllowedDownloadUri(uri, allowFtb)) throw Failure("重定向目标不在官方 CDN 允许范围。");
                if (!visited.Add(uri.AbsoluteUri)) throw Failure("检测到 CDN 重定向循环。");
                if (transport.DefaultRequestHeaders.Contains("x-api-key") || transport.DefaultRequestHeaders.Authorization is not null || transport.DefaultRequestHeaders.Contains("Cookie")) throw Failure("专用文件下载客户端携带了全局鉴权头，已拒绝下载。");
                // Clone only download range validators; never forward authorization, cookies, Host, or arbitrary headers.
                using var forwarded = new HttpRequestMessage(HttpMethod.Get, uri);
                forwarded.Headers.Range = request.Headers.Range; forwarded.Headers.IfRange = request.Headers.IfRange;
                HttpResponseMessage response;
                try { response = await transport.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (HttpRequestException) { throw Failure("官方 CDN 网络连接失败。"); }
                if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
                { response.Dispose(); throw Failure("专用 CDN 客户端发生未受校验的自动重定向，已拒绝文件。"); }
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    try
                    {
                        var status = (int)response.StatusCode;
                        if (status is not (301 or 302 or 303 or 307 or 308)) throw Failure($"不支持的 CDN 重定向状态 HTTP {status}。");
                        if (hops >= 3) throw Failure($"CDN 重定向超过 3 跳（HTTP {status}）。");
                        var location = response.Headers.Location;
                        if (location is null || location.OriginalString.Length > 2048 || location.OriginalString.Any(char.IsControl) || !Uri.TryCreate(uri, location, out var next)) throw Failure($"CDN 重定向缺少有效 Location（HTTP {status}）。");
                        if (!AllowedDownloadUri(next, allowFtb)) throw Failure($"CDN 重定向来源被拒绝（HTTP {status}）。");
                        uri = next;
                    }
                    finally { response.Dispose(); }
                    continue;
                }
                FailureReason = response.IsSuccessStatusCode ? null : $"官方 CDN 返回 HTTP {(int)response.StatusCode}。";
                return response;
            }
        }
        private HttpRequestException Failure(string reason) { FailureReason = reason; return new HttpRequestException(reason); }
    }
    private async Task<JsonDocument> GetJsonAsync(Uri uri, bool sendKey, CancellationToken ct)
    {
        if (http.DefaultRequestHeaders.Contains("x-api-key") || http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new LookupException("官方清单专用客户端不能携带全局凭据。");
        if (sendKey && (uri.Scheme != "https" || uri.Host != "api.curseforge.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)) throw new InvalidDataException("拒绝向未知来源发送 CurseForge 凭据。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SHOU-Launcher/LocalPackImport"); if (sendKey) request.Headers.Add("x-api-key", apiKey!);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest || response.RequestMessage?.RequestUri is { } final && final != uri) throw new LookupException("官方 API 重定向已拒绝，未继续发送凭据。");
        if (!response.IsSuccessStatusCode) throw new LookupException((sendKey ? "CurseForge" : "Modrinth 精确哈希查询") + $" 返回 HTTP {(int)response.StatusCode}，该文件尚未核实。");
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new LookupException("官方文件响应超过 1 MiB 上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); using var bytes = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false); if (count == 0) break; if (bytes.Length + count > 1024 * 1024) throw new LookupException("官方文件响应超过 1 MiB 上限。"); bytes.Write(buffer, 0, count); }
        return PackArchiveReader.Json(bytes.ToArray());
    }
    public static bool AllowedCurseForgeUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "edge.forgecdn.net" or "media.forgecdn.net" or "mediafilez.forgecdn.net" && uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal);
    private static long Number(JsonElement value, string key) => value.GetProperty(key).GetInt64();
    private static string Text(JsonElement value, string key, int maximum)
    { var text = value.GetProperty(key).GetString() ?? ""; if (text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("官方文件元数据文字字段无效。"); return text; }
    private static string Label(CurseForgePackReference item) => $"项目 {item.ProjectId} / 文件 {item.FileId}";
    private sealed class LookupException(string message) : Exception(message);
}
