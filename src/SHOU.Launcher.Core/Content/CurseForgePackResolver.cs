using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Content;

public sealed record CurseForgePackReference(int ProjectId, int FileId, bool Required);
public sealed record CurseForgePackFile(int ProjectId, int FileId, string Path, string Sha1, string? Sha512,
    long Size, Uri DownloadUri, bool FromModrinth);
public sealed record CurseForgePackResolution(IReadOnlyList<CurseForgePackFile> Files,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> BlockedFiles);

/// <summary>Caller-owned dedicated HttpClient MUST disable automatic redirects. API key is attached only to fixed CF requests.</summary>
public sealed class CurseForgePackResolver
{
    private readonly HttpClient http;
    private readonly string? apiKey;
    public CurseForgePackResolver(HttpClient http, string? apiKey)
    {
        this.http = http;
        if (http.DefaultRequestHeaders.Contains("x-api-key") || http.DefaultRequestHeaders.Authorization is not null) throw new ArgumentException("整合包专用客户端不能携带全局鉴权头。");
        if (apiKey is not null && (apiKey.Length > 8192 || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))) throw new ArgumentException("CurseForge API key 配置格式无效。");
        this.apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
    }
    public async Task<CurseForgePackResolution> ResolveAsync(IReadOnlyList<CurseForgePackReference> references, string gameVersion, string loader, CancellationToken ct = default)
    {
        if (references.Count > 512 || references.Any(r => r.ProjectId <= 0 || r.FileId <= 0)) throw new InvalidDataException("CurseForge 清单项目数量或身份无效。");
        var required = references.Where(r => r.Required).ToArray();
        if (apiKey is null) return new([], [], required.Select(r => Label(r) + "：未配置 CurseForge API key，尚未核实文件。").ToArray());
        var results = new Item?[required.Length]; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, required.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = timeout.Token }, async (index, token) =>
            { results[index] = await ResolveOneAsync(required[index], gameVersion, loader, references, token).ConfigureAwait(false); }).ConfigureAwait(false);
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
        return new(files, warnings, blocked);
    }
    private sealed record Item(CurseForgePackFile? File, IReadOnlyList<string> Warnings, string? Reason = null);
    private async Task<Item> ResolveOneAsync(CurseForgePackReference reference, string gameVersion, string loader, IReadOnlyList<CurseForgePackReference> all, CancellationToken ct)
    {
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
                : classId == 12 && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "resourcepacks/" : throw new InvalidDataException("本轮 CurseForge 文件仅支持 classId 6 模组 JAR 和 classId 12 资源包 ZIP。");
            var size = Number(data, "fileLength"); if (size is <= 0 or > PackArchiveReader.MaximumFileBytes) throw new InvalidDataException("官方文件大小超过单项 128 MiB 边界。");
            var hashes = data.GetProperty("hashes");
            if (hashes.ValueKind != JsonValueKind.Array || hashes.GetArrayLength() > 8) throw new InvalidDataException("官方哈希列表无效。");
            var sha1s = hashes.EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => Text(h, "value", 40)).ToArray();
            if (sha1s.Length != 1 || !ContentInstallFiles.Hash(sha1s[0], 40)) throw new InvalidDataException("官方文件缺少唯一有效 SHA-1。");
            var sha1 = sha1s[0].ToLowerInvariant(); var warnings = new List<string>();
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
            return new(null, warnings, "作者未提供第三方下载地址，Modrinth 也未找到同哈希同大小文件；未猜测下载链接。");
        }
        catch (OperationCanceledException) { throw; }
        catch (LookupException ex) { return new(null, [], ex.Message); }
        catch (HttpRequestException) { return new(null, [], "官方服务连接失败，请稍后重新预检。"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return new(null, [], ex is InvalidDataException ? ex.Message : "官方服务返回的文件元数据无效，未下载任何文件。"); }
    }
    public async Task DownloadAsync(CurseForgePackFile file, string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var label = DownloadLabel(file);
        if (!AllowedDownloadUri(file.DownloadUri)) throw new InvalidDataException(label + "：文件来源不在官方 CDN 允许范围。");
        if (!ContentInstallFiles.Hash(file.Sha1, 40) || file.Size is <= 0 or > PackArchiveReader.MaximumFileBytes || file.Sha512 is not null && !ContentInstallFiles.Hash(file.Sha512, 128))
            throw new InvalidDataException(label + "：文件哈希或大小无效。");
        using var redirects = new CdnRedirectHandler(http); using var downloads = new HttpClient(redirects) { Timeout = Timeout.InfiniteTimeSpan };
        try { await new VerifiedDownloader(downloads).DownloadAsync(new([file.DownloadUri], path, file.Sha1, Size: file.Size), progress, ct).ConfigureAwait(false); }
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
    private static bool AllowedDownloadUri(Uri uri) => AllowedCurseForgeUri(uri) || ContentInstallFiles.AllowedUri(uri);
    private static string DownloadLabel(CurseForgePackFile file)
    {
        var name = new string(Path.GetFileName(file.Path).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '+' or '(' or ')').Take(120).ToArray());
        return $"项目 {file.ProjectId} / 文件 {file.FileId}（{(name.Length == 0 ? "文件" : name)}）";
    }
    // This wrapper is used only by file downloads. API requests keep the caller's no-redirect transport directly.
    private sealed class CdnRedirectHandler(HttpClient transport) : HttpMessageHandler
    {
        internal string? FailureReason { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is null) throw Failure("文件下载仅接受无请求体的 GET。");
            var uri = request.RequestUri; var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var hops = 0; ; hops++)
            {
                if (!AllowedDownloadUri(uri)) throw Failure("重定向目标不在官方 CDN 允许范围。");
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
                        if (!AllowedDownloadUri(next)) throw Failure($"CDN 重定向来源被拒绝（HTTP {status}）。");
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
