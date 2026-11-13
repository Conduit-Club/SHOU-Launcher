using System.Text;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Content;
using SHOU.Launcher.Core.IO;
using SHOU.Launcher.Core.Minecraft;

namespace SHOU.Launcher.Core.Instances;

public static partial class LegacyLaunchSettings
{
    private static readonly HashSet<string> Keys = ["VersionAdvanceJvm", "VersionAdvanceGame", "VersionAdvanceClasspathHead", "VersionArgumentTitle", "VersionArgumentInfo", "VersionArgumentIndie", "VersionArgumentIndieV2", "VersionUseDebugLog4j2Config", "VersionAdvanceRun", "VersionArgumentJavaSelect", "VersionRamType", "VersionRamCustom", "VersionRamCustomInitial", "VersionAdvanceRenderer", "VersionAdvanceAssetsV2", "VersionAdvanceJava", "VersionAdvanceDisableJLW", "VersionAdvanceDisableLF", "VersionAdvanceDisableLwjglUnsafeAgent"];
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(string folder, CancellationToken ct)
    {
        var file = SafePath.Within(folder, "PCL/Setup.ini"); var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return values;
        var bytes = await WorldContentArchive.ReadFileAsync(folder, file, 256 * 1024, ct);
        var text = bytes.AsSpan().StartsWith(new byte[] {255,254}) ? Encoding.Unicode.GetString(bytes.AsSpan(2)) : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        foreach (var line in text.Split('\n'))
        {
            var a = line.IndexOf(':'); var b = line.IndexOf('='); var i = a < 0 ? b : b < 0 ? a : Math.Min(a, b);
            if (i > 0 && Keys.Contains(line[..i].Trim())) values[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return values;
    }
    public static InstanceDirectoryMode? Isolation(IReadOnlyDictionary<string,string> values)
    {
        if (values.TryGetValue("VersionArgumentIndieV2", out var v2) && bool.TryParse(v2, out var independent)) return independent ? InstanceDirectoryMode.Isolated : InstanceDirectoryMode.Shared;
        return values.GetValueOrDefault("VersionArgumentIndie") switch { "1" => InstanceDirectoryMode.Isolated, "2" => InstanceDirectoryMode.Shared, "0" => InstanceDirectoryMode.FollowGlobal, _ => null };
    }
    public static LegacyInstanceSettings Map(IReadOnlyDictionary<string,string> values, GameLaunchOptions basis, string? executableDirectory = null)
    {
        var options = basis with { InheritGlobal = false }; var warnings = new List<string>();
        foreach (var key in new[] { "VersionAdvanceJvm", "VersionAdvanceGame", "VersionAdvanceClasspathHead", "VersionArgumentTitle", "VersionArgumentInfo" })
        {
            if (values.GetValueOrDefault(key) is not { Length: > 0 } text) continue;
            var candidate = key switch { "VersionAdvanceJvm" => options with { JvmArguments = text }, "VersionAdvanceGame" => options with { GameArguments = text }, "VersionAdvanceClasspathHead" => options with { ClasspathPrepend = text }, "VersionArgumentTitle" => options with { WindowTitle = text }, _ => options with { CustomInfo = text } };
            try { GameSettingsPolicy.Validate(candidate); options = candidate; }
            catch (ArgumentException) { warnings.Add("部分旧高级参数不受支持，原文件保留，请手动核对。"); }
        }
        if (bool.TryParse(values.GetValueOrDefault("VersionUseDebugLog4j2Config"), out var debug)) options = options with { DebugLogging = debug };
        if (values.GetValueOrDefault("VersionAdvanceRun") is { Length: > 0 }) warnings.Add("旧前置命令未自动执行或导入，请明确选择程序及独立参数。");
        var java = ReadJavaPreference(values.GetValueOrDefault("VersionArgumentJavaSelect"), executableDirectory, warnings);
        var memory = ReadMemoryPreference(values, warnings);
        if (memory.Maximum is not null) options = options with { MinimumMemoryMiB = memory.Minimum };
        if (values.GetValueOrDefault("VersionRamType") == "0" && !basis.AutomaticMemory)
            warnings.Add("旧实例使用自动内存；当前独立内存界面仅支持跟随全局或固定值，请核对全局自动内存设置。原文件保留。");
        if (values.GetValueOrDefault("VersionAdvanceRenderer") is { Length: > 0 } renderer && renderer != "0") warnings.Add("旧 Windows 专用渲染器需要单独适配，当前保留游戏默认渲染器。");
        if (new[] { "VersionAdvanceAssetsV2", "VersionAdvanceJava", "VersionAdvanceDisableJLW", "VersionAdvanceDisableLF", "VersionAdvanceDisableLwjglUnsafeAgent" }.Any(k => bool.TryParse(values.GetValueOrDefault(k), out var enabled) && enabled)) warnings.Add("旧专属补丁或跳过检查的开关未覆盖当前安全引导和文件校验。");
        return new(options, Isolation(values), warnings.Distinct().ToArray(), java, memory.Maximum);
    }
}
