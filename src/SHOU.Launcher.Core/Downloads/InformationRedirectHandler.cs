namespace SHOU.Launcher.Core.Downloads;

/// <summary>Anonymous public information and release downloads, with explicit HTTPS redirects.</summary>
public sealed class InformationRedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var origin = request.RequestUri ?? throw new InvalidDataException("Missing public request URI.");
        var current = origin;
        for (var hop = 0; hop <= 5; hop++)
        {
            if (current.Scheme != "https" || !string.IsNullOrEmpty(current.UserInfo)) throw new InvalidDataException("Public request requires HTTPS without credentials.");
            if (current.Host != origin.Host && !(origin.Host == "github.com" && origin.AbsolutePath.StartsWith("/Conduit-Club/SHOU-Launcher/releases/download/", StringComparison.Ordinal)
                && current.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("Unexpected public download redirect.");
            using var next = new HttpRequestMessage(request.Method, current);
            foreach (var name in new[] { "User-Agent", "Accept", "Range", "If-Range" }) if (request.Headers.TryGetValues(name, out var values)) next.Headers.TryAddWithoutValidation(name, values);
            var response = await base.SendAsync(next, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            if (hop == 5 || response.Headers.Location is not { } location) { response.Dispose(); throw new InvalidDataException("Too many public request redirects."); }
            current = location.IsAbsoluteUri ? location : new Uri(current, location); response.Dispose();
        }
        throw new InvalidDataException("Too many public request redirects.");
    }
}
