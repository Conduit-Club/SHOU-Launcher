using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Loaders;

public sealed partial class InstallerCatalogReader
{
    private static bool Additional(string loader) => loader is "optifine" or "cleanroom" or "labymod" or "liteloader";
    private async Task<InstallerCatalog> ReadAdditionalAsync(string loader, CancellationToken ct)
    {
        var rows = new List<InstallerVersion>();
        if (loader == "optifine")
        {
            // Same data contract as the legacy business reader; no legacy UI dependencies.
            var data = JsonNode.Parse(await reader.TextAsync(new("https://bmclapi2.bangbang93.com/optifine/versionList"), 2 * 1024 * 1024, ct).ConfigureAwait(false)) as JsonArray ?? throw new InvalidDataException("OptiFine 目录无效。");
            foreach (var node in data.Take(5000))
            {
                var game = Coordinate((string?)node?["mcversion"] ?? ""); var type = Coordinate((string?)node?["type"] ?? ""); var patch = Coordinate((string?)node?["patch"] ?? "");
                if (!Regex.IsMatch(type, @"\AHD_U(?:_[A-Z][0-9]+)?\z", RegexOptions.CultureInvariant)) continue;
                var version = (type == "HD_U" ? "" : type[5..] + "_") + patch;
                var row = new InstallerVersion(loader, game, version, type + "_" + patch, Channel: patch.Contains("pre", StringComparison.OrdinalIgnoreCase) ? "preview" : "stable");
                if ((string?)node?["filename"] != InstallerFileName(row)) throw new InvalidDataException("OptiFine 文件名称与版本不匹配。");
                rows.Add(row);
            }
        }
        else if (loader == "cleanroom")
        {
            var text = await reader.TextAsync(new("https://maven.cleanroommc.com/com/cleanroommc/cleanroom/maven-metadata.xml"), 1024 * 1024, ct).ConfigureAwait(false);
            if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cleanroom 目录不接受 DTD。");
            foreach (var version in XDocument.Parse(text).Descendants("version").Select(n => Coordinate(n.Value)).Distinct()) rows.Add(new(loader, "1.12.2", version, version, Channel: version.Contains('-') ? "preview" : "stable"));
        }
        else if (loader == "labymod")
        {
            foreach (var channel in new[] { "production", "snapshot" })
            {
                var data = JsonNode.Parse(await reader.TextAsync(new("https://releases.r2.labymod.net/api/v1/manifest/" + channel + "/latest.json"), 1024 * 1024, ct).ConfigureAwait(false)) ?? throw new InvalidDataException("LabyMod 目录无效。");
                var version = Coordinate((string?)data["labyModVersion"] ?? ""); var commit = Coordinate((string?)data["commitReference"] ?? "");
                rows.Add(new(loader, "版本列表", version, commit, Published: (long?)data["releaseTime"] is { } time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : null, Latest: true, Channel: channel));
            }
            return new(rows);
        }
        else if (loader == "liteloader")
        {
            var data = JsonNode.Parse(await reader.TextAsync(new("https://dl.liteloader.com/versions/versions.json"), 1024 * 1024, ct).ConfigureAwait(false))?["versions"] as JsonObject ?? throw new InvalidDataException("LiteLoader 目录无效。");
            foreach (var pair in data)
            {
                var game = Coordinate(pair.Key); var latest = (pair.Value?["artefacts"] ?? pair.Value?["snapshots"])?["com.mumfrey:liteloader"]?["latest"];
                if (latest is null) continue;
                var version = Coordinate((string?)latest["version"] ?? "");
                rows.Add(new(loader, game, version, version, Published: long.TryParse((string?)latest["timestamp"], out var stamp) ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null,
                    Channel: (string?)latest["stream"] == "RELEASE" ? "stable" : "preview", Latest: true));
            }
        }
        var ordered = rows.OrderByDescending(r => NumericKey(r.GameVersion), StringComparer.Ordinal).ThenByDescending(r => NumericKey(r.Version), StringComparer.Ordinal).ToArray();
        return new(ordered.GroupBy(r => r.GameVersion).SelectMany(g => g.Select((r, i) => r with { Latest = i == 0 })).ToArray());
    }
    public static string InstallerFileName(InstallerVersion row) => row.Loader switch
    {
        "optifine" => (row.Channel == "preview" ? "preview_" : "") + $"OptiFine_{Coordinate(row.GameVersion)}_{Coordinate(row.ArtifactVersion)}.jar",
        "cleanroom" => $"cleanroom-{Coordinate(row.ArtifactVersion)}-installer.jar",
        "labymod" when row.Channel is "production" or "snapshot" => $"LabyMod-{Coordinate(row.Version)}-{row.Channel}-installer.jar",
        "liteloader" => "liteloader-installer-" + Coordinate(row.GameVersion) + (row.GameVersion is "1.8" or "1.9" ? ".0" : "") + (row.GameVersion switch { "1.7.10" or "1.7.2" or "1.6.2" => "-04.jar", "1.6.4" or "1.5.2" => "-01.jar", _ => "-00-SNAPSHOT.jar" }),
        _ => Uri.UnescapeDataString(Path.GetFileName(ArtifactUri(row).AbsolutePath))
    };
    private static Uri AdditionalArtifact(InstallerVersion row)
    {
        var artifact = Coordinate(row.ArtifactVersion); Coordinate(row.Version); Coordinate(row.GameVersion == "版本列表" ? "list" : row.GameVersion);
        return row.Loader switch
        {
            "optifine" when artifact == "HD_U_" + row.Version && row.Channel is "preview" or "stable" => new("https://optifine.net/adloadx?f=" + Uri.EscapeDataString(InstallerFileName(row))),
            "cleanroom" when row.GameVersion == "1.12.2" && artifact == row.Version => new($"https://maven.cleanroommc.com/com/cleanroommc/cleanroom/{Uri.EscapeDataString(artifact)}/{InstallerFileName(row)}"),
            "labymod" when row.Channel is "production" or "snapshot" => new("https://releases.labymod.net/api/v1/installer/" + row.Channel + "/java"),
            "liteloader" when artifact == row.Version => row.GameVersion is "1.7.10" or "1.7.2" or "1.6.4" or "1.6.2" or "1.5.2"
                ? new("https://dl.liteloader.com/redist/" + row.GameVersion + "/" + InstallerFileName(row))
                : new("https://jenkins.liteloader.com/job/LiteLoaderInstaller%20" + row.GameVersion + "/lastSuccessfulBuild/artifact/" + (row.GameVersion == "1.8" ? "ant/dist/" : "build/libs/") + InstallerFileName(row)),
            _ => throw new InvalidDataException("安装器身份无效。")
        };
    }
    private async Task SaveAdditionalAsync(InstallerVersion row, string destination, string stagingRoot, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var uri = AdditionalArtifact(row);
        // Re-read the fixed catalog before saving: a row cannot supply arbitrary URLs or checksum claims.
        var catalog = await ReadAdditionalAsync(row.Loader, ct).ConfigureAwait(false);
        if (!catalog.Versions.Any(v => v.GameVersion == row.GameVersion && v.Version == row.Version && v.ArtifactVersion == row.ArtifactVersion && v.Channel == row.Channel)) throw new InvalidDataException("安装器版本已变化，请刷新目录后重试。");
        string? expectedSha1 = null; Uri? optiMirror = null;
        if (row.Loader == "cleanroom")
        {
            expectedSha1 = (await reader.TextAsync(new(uri.AbsoluteUri + ".sha1"), 4096, ct).ConfigureAwait(false)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!ValidSha1(expectedSha1)) throw new InvalidDataException("Cleanroom 官方 SHA-1 无效。");
        }
        else if (row.Loader == "optifine")
        {
            // BMCLAPI identifies its distribution by SHA-1. Prefer the official file; retain the exact delegated URL only for unavailable official files.
            using var identityTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); identityTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var path = "https://bmclapi2.bangbang93.com/maven/com/optifine/" + row.GameVersion + "/" + InstallerFileName(row);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, identityTimeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 307 or 308) || response.Headers.Location is not { IsAbsoluteUri: true } location || location.Scheme != "https" || location.UserInfo.Length != 0) throw new InvalidDataException("OptiFine 镜像未提供文件校验身份。");
            expectedSha1 = Regex.Match(location.AbsolutePath, @"\A/(?:download|bmclapi)/([a-fA-F0-9]{40})(?:/.*)?\z", RegexOptions.CultureInvariant).Groups[1].Value;
            if (!ValidSha1(expectedSha1)) throw new InvalidDataException("OptiFine 镜像文件校验身份无效。");
            optiMirror = location;
            var page = await reader.TextAsync(uri, 512 * 1024, ct).ConfigureAwait(false);
            var link = WebUtility.HtmlDecode(Regex.Match(page, @"downloadx\?f=[^""'<>\s]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Value);
            if (!Uri.TryCreate(new Uri("https://optifine.net/"), link, out var download) || download.Host != "optifine.net" || download.Scheme != "https" || !download.IsDefaultPort || download.AbsolutePath != "/downloadx" || !download.Query.StartsWith("?f=" + InstallerFileName(row) + "&", StringComparison.Ordinal)) throw new InvalidDataException("OptiFine 官方下载链接无效。");
            uri = download;
        }
        var output = Path.GetFullPath(destination); VerifiedDownloader.RejectLink(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var stage = SafePath.Within(stagingRoot, "installer-downloads/" + Guid.NewGuid().ToString("N") + ".jar");
        var publishing = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileName(output) + ".shou-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var opened = await OpenInstallerAsync(uri, optiMirror, timeout.Token).ConfigureAwait(false); uri = opened.Uri;
            using var response = opened.Response;
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri || response.Content.Headers.ContentLength is > 64 * 1024 * 1024) throw new InvalidDataException("安装器发生未校验的重定向或超过大小限制。");
            Directory.CreateDirectory(Path.GetDirectoryName(stage)!); VerifiedDownloader.RejectLink(stage);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var bytes = new byte[131072]; long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (count == 0) break;
                    total += count; if (total > 64 * 1024 * 1024) throw new InvalidDataException("安装器超过 64 MiB。");
                    await target.WriteAsync(bytes.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new(stage, "Downloading", total, response.Content.Headers.ContentLength, uri.Host));
                }
                if (total < 1024 || response.Content.Headers.ContentLength is { } size && total != size) throw new InvalidDataException("安装器大小无效或下载不完整。");
            }
            if (expectedSha1 is not null)
            {
                await using var input = File.OpenRead(stage);
                if (!Convert.ToHexStringLower(await SHA1.HashDataAsync(input, timeout.Token).ConfigureAwait(false)).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装器 SHA-1 校验失败。");
            }
            ValidateJar(stage);
            VerifiedDownloader.RejectLink(publishing); VerifiedDownloader.RejectLink(output);
            await using (var input = File.OpenRead(stage))
            await using (var target = new FileStream(publishing, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await input.CopyToAsync(target, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(output); File.Move(publishing, output, true);
            progress?.Report(new(output, "Completed", new FileInfo(output).Length, new FileInfo(output).Length, uri.Host));
        }
        finally
        { foreach (var path in new[] { stage, publishing }) if (File.Exists(path)) { VerifiedDownloader.RejectLink(path); File.Delete(path); } }
    }
    private async Task<(HttpResponseMessage Response, Uri Uri)> OpenInstallerAsync(Uri official, Uri? optiMirror, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, official);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (optiMirror is null || response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType != "text/html" && response.Content.Headers.ContentLength is not (< 1024)) return (response, official);
        response.Dispose();
        // The one-time allowlist is exactly the hash-bearing HTTPS URL delegated by the fixed BMCLAPI endpoint.
        // No CDN redirect is followed, no API/header is forwarded, and local/private destinations are rejected.
        if (!optiMirror.IsDefaultPort || optiMirror.UserInfo.Length != 0 || optiMirror.Fragment.Length != 0 || optiMirror.AbsoluteUri.Length > 4096 || IPAddress.TryParse(optiMirror.Host, out _)) throw new InvalidDataException("OptiFine 镜像节点地址无效。");
        var addresses = await Dns.GetHostAddressesAsync(optiMirror.DnsSafeHost, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(PrivateAddress)) throw new InvalidDataException("OptiFine 镜像节点不能指向本机或私有网络。");
        using var mirrorRequest = new HttpRequestMessage(HttpMethod.Get, optiMirror);
        return (await http.SendAsync(mirrorRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false), optiMirror);
    }
    private static bool PrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] is 0 or 168
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        // 198.18/15 is deliberately not RFC1918: system proxies commonly map public names into that fake-IP range.
    }
    private static bool ValidSha1(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static void ValidateJar(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is 0 or > 20000 || !archive.Entries.Any(e => e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase)) || archive.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new InvalidDataException("下载文件不是支持的完整 Java 安装器归档。");
        foreach (var entry in archive.Entries)
            if (entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Split('/').Any(p => p is "." or ".." || p.Contains(':'))) throw new InvalidDataException("安装器归档包含不安全路径。");
    }
}
