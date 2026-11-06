using System.Text.Json.Nodes;
using System.Xml.Linq;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.Loaders;

/// <summary>Read-only installer catalogs. Browsing never creates instances or starts Java.</summary>
public sealed class InstallerCatalogReader(HttpClient http)
{
    private readonly LoaderCatalog reader = new(http);
    public async Task<InstallerCatalog> ReadAsync(string loader, CancellationToken ct)
    {
        if (loader is "fabric" or "legacyfabric")
        {
            var meta = loader == "fabric" ? "meta.fabricmc.net" : "meta.legacyfabric.net";
            var data = JsonNode.Parse(await reader.TextAsync(new("https://" + meta + "/v2/versions/installer"), 1024 * 1024, ct)) as JsonArray
                ?? throw new InvalidDataException("Fabric 安装器目录无效。");
            return new(data.Take(200).Select(x => new InstallerVersion(loader, "通用安装器", SafePath.Identifier((string?)x?["version"] ?? ""),
                SafePath.Identifier((string?)x?["version"] ?? ""), (bool?)x?["stable"] == true)).ToArray());
        }
        var url = loader switch
        {
            "forge" => "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
            "neoforge" => "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
            "quilt" => "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/maven-metadata.xml",
            "legacyfabric" => "https://maven.legacyfabric.net/net/legacyfabric/fabric-installer/maven-metadata.xml",
            _ => null
        };
        if (url is null) return new([], loader switch
        {
            "optifine" => "OptiFine 尚无已接入的可校验安装器目录，可前往官网下载。",
            "cleanroom" => "Cleanroom 独立安装器的来源与校验接口仍待接入，可前往官网下载。",
            "labymod" => "LabyMod 独立安装器的来源与校验接口仍待接入，可前往官网下载。",
            "liteloader" => "LiteLoader 历史安装器的来源与校验接口仍待接入。",
            _ => "此安装器目录暂不可用。"
        });
        var text = await reader.TextAsync(new(url), 4 * 1024 * 1024, ct);
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装器目录不接受 DTD。");
        var versions = XDocument.Parse(text).Descendants("version").Select(x => SafePath.Identifier(x.Value)).Distinct(StringComparer.Ordinal).ToArray();
        if (versions.Length > 20000) throw new InvalidDataException("安装器目录数量超过限制。");
        var rows = new List<InstallerVersion>();
        foreach (var artifact in versions)
        {
            if (loader == "forge")
            {
                var separator = artifact.IndexOf('-');
                if (separator <= 0 || separator == artifact.Length - 1) continue;
                rows.Add(new(loader, artifact[..separator], artifact[(separator + 1)..], artifact));
            }
            else if (loader == "neoforge")
            {
                var parts = artifact.Split('.', '-');
                if (parts.Length < 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) continue;
                var game = major >= 26 ? $"{major}.{minor}" : $"1.{major}" + (minor == 0 ? "" : "." + minor);
                rows.Add(new(loader, game, artifact, artifact));
            }
            else rows.Add(new(loader, "通用安装器", artifact, artifact));
        }
        if (loader == "forge")
        {
            try
            {
                var promotions = JsonNode.Parse(await reader.TextAsync(new("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"), 256 * 1024, ct))?["promos"];
                rows = rows.Select(r => r with { Recommended = (string?)promotions?[r.GameVersion + "-recommended"] == r.Version }).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException) { }
        }
        return new(rows.OrderByDescending(r => NumericKey(r.GameVersion), StringComparer.Ordinal)
            .ThenByDescending(r => NumericKey(r.Version), StringComparer.Ordinal).ToArray());
    }
    public static Uri ArtifactUri(InstallerVersion row)
    {
        var artifact = SafePath.Identifier(row.ArtifactVersion); var version = SafePath.Identifier(row.Version);
        if (row.Loader == "forge" && artifact != SafePath.Identifier(row.GameVersion) + "-" + version)
            throw new InvalidDataException("Forge 安装器身份不匹配。");
        if (row.Loader != "forge" && artifact != version) throw new InvalidDataException("安装器版本身份不匹配。");
        return new(row.Loader switch
        {
            "forge" => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{artifact}/forge-{artifact}-installer.jar",
            "neoforge" => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{artifact}/neoforge-{artifact}-installer.jar",
            "fabric" => $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{version}/fabric-installer-{version}.jar",
            "quilt" => $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/{version}/quilt-installer-{version}.jar",
            "legacyfabric" => $"https://maven.legacyfabric.net/net/legacyfabric/fabric-installer/{version}/fabric-installer-{version}.jar",
            _ => throw new NotSupportedException("此安装器尚不支持直接另存。")
        });
    }
    public async Task SaveAsync(InstallerVersion row, string destination, string stagingRoot, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(destination) || !Path.GetExtension(destination).Equals(".jar", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择本机 JAR 文件保存位置。");
        var uri = ArtifactUri(row);
        var text = await reader.TextAsync(new(uri.AbsoluteUri + ".sha1"), 4096, ct);
        var sha = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha?.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("官方安装器校验值无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var size = response.Content.Headers.ContentLength;
        if (size is not (> 0 and <= 64 * 1024 * 1024)) throw new InvalidDataException("官方安装器缺少有效大小或超过 64 MiB。");
        var output = Path.GetFullPath(destination);
        VerifiedDownloader.RejectLink(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var stage = SafePath.Within(stagingRoot, "installer-downloads/" + Guid.NewGuid().ToString("N") + ".jar");
        var publishing = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileName(output) + ".shou-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await new VerifiedDownloader(http).DownloadAsync(new([uri], stage, sha, Size: size), progress, ct).ConfigureAwait(false);
            VerifiedDownloader.RejectLink(output); VerifiedDownloader.RejectLink(publishing);
            await using (var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var target = new FileStream(publishing, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            { await input.CopyToAsync(target, ct).ConfigureAwait(false); await target.FlushAsync(ct).ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested(); VerifiedDownloader.RejectLink(output);
            File.Move(publishing, output, true);
        }
        finally
        {
            foreach (var path in new[] { stage, stage + ".part", stage + ".part.resume.json", stage + ".download.lock", publishing })
            { if (File.Exists(path)) { VerifiedDownloader.RejectLink(path); File.Delete(path); } }
        }
    }
    private static string NumericKey(string version) => string.Join('.', version.Split('.', '-').Select(p => int.TryParse(p, out var n) ? n.ToString("D9", System.Globalization.CultureInfo.InvariantCulture) : p));
}
