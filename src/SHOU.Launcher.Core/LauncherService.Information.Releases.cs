using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService
{
    public async Task<IReadOnlyList<LauncherRelease>> CheckLauncherReleasesAsync(CancellationToken cancellationToken = default)
    {
        try { return await ReadReleasesApiAsync(cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            // This public feed is requested only by the same explicit manual check. No credentials or assets.
            var bytes = await ReadPublicDocumentAsync(new("https://github.com/Conduit-Club/SHOU-Launcher/releases.atom"), 2 * 1024 * 1024, cancellationToken, "application/atom+xml").ConfigureAwait(false);
            try
            {
                using var stream = new MemoryStream(bytes);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var document = XDocument.Load(reader); XNamespace atom = "http://www.w3.org/2005/Atom";
                if (document.Root?.Name != atom + "feed") throw new InvalidDataException("发行订阅格式无效。");
                var releases = new List<LauncherRelease>();
                foreach (var entry in document.Root.Elements(atom + "entry").Take(20))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var links = entry.Elements(atom + "link").Where(e => (string?)e.Attribute("rel") == "alternate").ToArray();
                    if (links.Length != 1 || !Uri.TryCreate((string?)links[0].Attribute("href"), UriKind.Absolute, out var page)
                        || !IsRepositoryPage(page) || !page.IsDefaultPort || page.Query.Length != 0 || page.Fragment.Length != 0
                        || !page.AbsolutePath.StartsWith("/Conduit-Club/SHOU-Launcher/releases/tag/", StringComparison.Ordinal)) throw new InvalidDataException("发行页面地址无效。");
                    var tag = Uri.UnescapeDataString(page.AbsolutePath["/Conduit-Club/SHOU-Launcher/releases/tag/".Length..]);
                    if (tag.Length is < 1 or > 160 || tag.Any(char.IsControl) || !DateTimeOffset.TryParse(entry.Element(atom + "updated")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var published)) throw new InvalidDataException("发行版本或日期无效。");
                    var title = entry.Element(atom + "title")?.Value ?? tag; if (title.Length > 512) title = title[..512];
                    var content = entry.Element(atom + "content")?.Value ?? "";
                    if (content.Length > 65536) content = content[..65536];
                    content = Regex.Replace(content, "</(?:p|li|h[1-6])>|<br\\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
                    content = WebUtility.HtmlDecode(Regex.Replace(content, "<[^>]*>", "", RegexOptions.NonBacktracking)).Trim();
                    releases.Add(new(tag, title, content[..Math.Min(32768, content.Length)], published, page, null));
                }
                return releases.OrderByDescending(r => r.Published).ToArray();
            }
            catch (XmlException error) { throw new InvalidDataException("发行订阅无法读取。可前往项目发行页面查看。", error); }
        }
    }
}
