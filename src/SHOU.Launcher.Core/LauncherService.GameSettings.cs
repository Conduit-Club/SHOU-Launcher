using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Minecraft;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core;

public sealed partial class LauncherService : IGameSettingsService, IJavaManagementService
{
    public async Task<JavaInstallation> InspectJavaAsync(string executable, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var name = Path.GetFileName(executable);
        if (name is not ("java" or "java.exe" or "javaw.exe")) throw new ArgumentException("请选择 Java 的 java / java.exe 可执行文件。");
        if (name == "javaw.exe" && File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "java.exe"))) executable = Path.Combine(Path.GetDirectoryName(executable)!, "java.exe");
        var java = await platform.JavaDiscovery.ProbeAsync(executable, cancellationToken);
        Set(s => s with { JavaInstallations = s.JavaInstallations.Where(j => !j.ExecutablePath.Equals(java.ExecutablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Append(java).ToArray() });
        return java;
    }
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
