using System;

namespace PCL.Core.Conduit;

/// <summary>Keep API keys on the exact official HTTPS API/CDN hosts.</summary>
public static class CurseForgeAccess
{
    public static bool RequiresApiKey(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "api.curseforge.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net";
}
