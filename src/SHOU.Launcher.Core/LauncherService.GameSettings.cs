using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IGameSettingsService
{
    public MemoryStatus? ReadMemoryStatus()
    {
        try { return platform.MemoryInfo?.Read(); }
        catch (Exception ex) when (ex is IOException or FormatException or System.ComponentModel.Win32Exception) { return null; }
    }
    private string NewGameDirectory(string loader)
    {
        var root = Snapshot.Settings.GameRoot ?? Paths.InstancesDirectory;
        var isolation = Snapshot.Settings.LaunchOptions?.Isolation ?? InstanceIsolation.All;
        return SafePath.Within(root, isolation == InstanceIsolation.Shared || isolation == InstanceIsolation.Modded && loader == "vanilla" ? "shared-game" : Guid.NewGuid().ToString("N"));
    }
    private IReadOnlyList<DownloadSource> EffectiveSources()
    {
        var parsed = DownloadSources.Parse(Snapshot.Settings.DownloadSourcesJson);
        var preference = Snapshot.Settings.Downloads?.SourcePreference ?? DownloadSourcePreference.Custom;
        if (preference == DownloadSourcePreference.Custom) return parsed;
        parsed = DownloadSources.Parse("");
        foreach (var source in parsed) source.Priority += ((source.Layout == SourceLayout.Official) == (preference == DownloadSourcePreference.OfficialFirst)) ? -1000 : 1000;
        return parsed;
    }
}
