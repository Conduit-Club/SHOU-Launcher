using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Conduit;

public static class ModDownloadSources
{
    /// <summary>GET, read-only batch POST and file downloads share the same source order.</summary>
    public static IReadOnlyList<string> Resolve(string url, IReadOnlyList<DownloadSource>? sourceOrder,
        int legacySolution = 1, bool allowMirror = true)
    {
        if (sourceOrder is not null)
            return DownloadSources.Resolve([url], sourceOrder)
                .Select(s => s.Url).Where(u => allowMirror || u == url).Distinct().ToArray();

        if (!allowMirror) return [url];
        var mirror = url;
        foreach (var (official, replacement) in new[]
        {
            ("https://api.curseforge.com", "https://mod.mcimirror.top/curseforge"),
            ("https://api.modrinth.com", "https://mod.mcimirror.top/modrinth"),
            ("https://edge.forgecdn.net", "https://mod.mcimirror.top"),
            ("https://mediafilez.forgecdn.net", "https://mod.mcimirror.top"),
            ("https://cdn.modrinth.com", "https://mod.mcimirror.top")
        })
        {
            if (!DownloadSources.Matches(url, official)) continue;
            mirror = replacement + url[official.Length..];
            break;
        }
        // Balanced mode now gives the authenticated official source a full attempt before fallback.
        return (legacySolution == 0 ? new[] { mirror, url } : new[] { url, mirror }).Distinct().ToArray();
    }
}
