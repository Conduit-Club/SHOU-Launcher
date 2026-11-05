using System.Text.Json.Nodes;
using System.Xml.Linq;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Loaders;

public sealed class LoaderCatalog(HttpClient http)
{
    public async Task<IReadOnlyList<LoaderVersionChoice>> GetAsync(string loader, string gameVersion, CancellationToken ct)
    {
        SafePath.Identifier(gameVersion);
        if (loader == "quilt")
        {
            var data = JsonNode.Parse(await TextAsync(new("https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(gameVersion)), 4 * 1024 * 1024, ct)) as JsonArray
                ?? throw new InvalidDataException("Quilt目录不是数组。");
            return data.Take(2000).Select(row =>
            {
                var version = SafePath.Identifier((string?)row?["loader"]?["version"] ?? throw new InvalidDataException("Quilt版本缺失。"));
                return new LoaderVersionChoice("quilt", version, (bool?)row?["loader"]?["stable"] ?? !version.Contains('-'));
            }).OrderByDescending(v => v.Stable).ThenByDescending(v => NumericKey(v.Version), StringComparer.Ordinal).ToArray();
        }
        var uri = loader switch
        {
            "forge" => new Uri("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
            "neoforge" => new Uri("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
            _ => throw new PlatformNotSupportedException("尚未提供此加载器的目录。")
        };
        var text = await TextAsync(uri, 4 * 1024 * 1024, ct);
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("加载器目录不接受DTD。");
        var versions = XDocument.Parse(text).Descendants("version").Select(x => x.Value).Take(50000);
        var prefix = loader == "forge" ? gameVersion + "-" : NeoPrefix(gameVersion);
        return versions.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)
            .Select(v => new LoaderVersionChoice(loader, SafePath.Identifier(loader == "forge" ? v[prefix.Length..] : v), !v.Any(char.IsLetter)))
            .OrderByDescending(v => NumericKey(v.Version), StringComparer.Ordinal).Take(2000).ToArray();
    }
    public static string NeoPrefix(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, out _))) throw new PlatformNotSupportedException("NeoForge仅支持正式数字版本目录。");
        return parts[0] == "1" ? parts[1] + "." + (parts.Length == 3 ? parts[2] : "0") + "." : parts[0] + "." + parts[1] + ".";
    }
    private static string NumericKey(string version) => string.Join('.', version.Split('.', '-').Select(p => int.TryParse(p, out var number) ? number.ToString("D9", System.Globalization.CultureInfo.InvariantCulture) : p));
    public async Task<string> TextAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("SHOU-Launcher/1.5 (Conduit-Club)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode(); if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("加载器响应超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); if (count == 0) break; if (output.Length + count > maximum) throw new InvalidDataException("加载器响应超过大小限制。"); output.Write(buffer, 0, count); }
        return new System.Text.UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
