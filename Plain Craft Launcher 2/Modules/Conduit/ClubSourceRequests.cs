using PCL.Core.App;
using PCL.Core.Conduit;
using PCL.Network;
using System.Collections.Concurrent;

namespace PCL;

public static class ClubSourceRequests
{
    private static readonly ConcurrentDictionary<string, string> VersionIds = new();

    public static ModDownload.DlClientListResult MinecraftManifest(string? requiredVersion)
    {
        var sources = DownloadSources.Resolve([DownloadSources.ManifestUrl], DownloadSources.Parse(Config.Download.Sources));
        var result = DownloadSources.FindManifest(sources,
            url => Requester.FetchString(url, new RequestParam { BypassSourceOrder = true, Timeout = 15000 }), requiredVersion,
            (source, ex) => ModBase.Log($"[Conduit] 版本清单源 {source.Name} 不可用，尝试下一源：{ex.Message}", ModBase.LogLevel.Debug));
        ModBase.Log($"[Conduit] 版本清单来源：{result.Source.Name}，版本数：{result.Manifest["versions"]!.AsArray().Count}");
        foreach (var entry in result.Manifest["versions"]!.AsArray())
            VersionIds[(string)entry!["url"]!] = (string)entry["id"]!;
        return new ModDownload.DlClientListResult { Value = result.Manifest, SourceName = result.Source.Name, IsOfficial = result.Source.Url == DownloadSources.ManifestUrl };
    }

    public static void InvalidateManifest()
    {
        // Do not let an in-flight request using the old ordering become a permanent cache.
        var loader = ModDownload.dlClientListLoader;
        loader.Abort();
        lock (loader.lockState) loader.State = ModBase.LoadState.Waiting;
        VersionIds.Clear();
    }

    public static IEnumerable<string> ExpandVersionMetadata(IEnumerable<string> input)
    {
        var urls = input.ToList();
        var settings = DownloadSources.Parse(Config.Download.Sources);
        foreach (var url in urls.ToArray())
        {
            if (!VersionIds.TryGetValue(url, out var id)) continue;
            foreach (var source in DownloadSources.Resolve([DownloadSources.ManifestUrl], settings))
                try
                {
                    var manifest = DownloadSources.ValidateManifest(Requester.FetchString(source.Url,
                        new RequestParam { BypassSourceOrder = true, Timeout = 10000 }), id);
                    urls.Add((string)manifest["versions"]!.AsArray().First(v => (string?)v?["id"] == id)!["url"]!);
                }
                catch (Exception ex) { ModBase.Log(ex, "[Conduit] 备用版本元数据源不可用", ModBase.LogLevel.Debug); }
        }
        return urls.Distinct().ToArray();
    }
}
