using System.Globalization;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Instances;

public static partial class LegacyLaunchSettings
{
    public static LauncherSettings MapGlobalRuntimeSettings(IReadOnlyDictionary<string, string> values,
        LauncherSettings basis, string? executableDirectory, out IReadOnlyList<string> notices)
    {
        var warnings = new List<string>();
        var java = ReadJavaPreference(values.GetValueOrDefault("LaunchArgumentJavaSelect"), executableDirectory, warnings);
        var normalized = new Dictionary<string, string>();
        foreach (var suffix in new[] { "Type", "Custom", "CustomInitial" })
            if (values.TryGetValue("LaunchRam" + suffix, out var value)) normalized["VersionRam" + suffix] = value;
        var memory = ReadMemoryPreference(normalized, warnings);
        var options = basis.LaunchOptions ?? new(AutomaticMemory: true);
        if (memory.Maximum is not null) options = options with { AutomaticMemory = false };
        else if (normalized.GetValueOrDefault("VersionRamType") == "0") options = options with { AutomaticMemory = true };
        notices = warnings;
        return basis with { JavaExecutable = java ?? basis.JavaExecutable, MemoryMiB = memory.Maximum ?? basis.MemoryMiB,
            MinimumMemoryMiB = memory.Maximum is not null ? memory.Minimum : basis.MinimumMemoryMiB, LaunchOptions = options };
    }

    private static (int? Maximum, int? Minimum) ReadMemoryPreference(IReadOnlyDictionary<string, string> values, List<string> warnings)
    {
        var mode = values.GetValueOrDefault("VersionRamType");
        if (mode is null or "" or "0" or "2") return (null, null);
        if (mode != "1" || !TryMemoryTick(values.GetValueOrDefault("VersionRamCustom") ?? "15", out var maximum))
        {
            warnings.Add("旧自定义内存超出支持范围或格式无效，未自动覆盖；原设置保留，请手动核对。");
            return (null, null);
        }
        var initial = values.GetValueOrDefault("VersionRamCustomInitial");
        if (initial is null or "" or "0") return (maximum, null);
        if (TryMemoryTick(initial, out var minimum)) return (maximum, Math.Min(minimum, maximum));
        warnings.Add("旧初始内存格式无效或超出支持范围，未自动设置初始堆；原设置保留。");
        return (maximum, null);
    }

    private static bool TryMemoryTick(string text, out int mib)
    {
        mib = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var tick) || tick is < 0 or > 41) return false;
        // Original PCL GetRamFromTick business rule; these are slider ticks, never MiB.
        mib = tick switch { <= 12 => (tick + 3) * 1024 / 10, <= 25 => (tick - 12) * 512 + 1536,
            <= 33 => (tick - 25) * 1024 + 8192, _ => (tick - 33) * 2048 + 16384 };
        return mib is >= 512 and <= 32768;
    }

    private static string? ReadJavaPreference(string? raw, string? executableDirectory, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw is "使用全局设置" or "自动选择") return null;
        try
        {
            if (raw.Length > 32768) throw new InvalidDataException();
            string? path = raw; var relative = false;
            if (raw.TrimStart().StartsWith('{'))
            {
                using var json = JsonDocument.Parse(raw, new() { MaxDepth = 16 });
                var node = json.RootElement;
                if (node.ValueKind != JsonValueKind.Object || node.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException();
                string? Text(string key) => node.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                var kind = Text("kind");
                if (kind is "auto" or "global") return null;
                if (kind is not (null or "exist" or "relative")) throw new InvalidDataException();
                relative = kind == "relative";
                path = relative ? Text("RelativePath") : Text("JavaExePath") ?? Text("Path");
            }
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) throw new InvalidDataException();
            if (path.StartsWith('$')) { relative = true; path = path[1..].TrimStart('/', '\\'); }
            if (relative)
            {
                if (executableDirectory is null || Path.IsPathFullyQualified(path)) throw new InvalidDataException();
                path = SafePath.Within(executableDirectory, path);
            }
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException();
            path = Path.GetFullPath(path); SafePath.RejectLinks(path);
            if (Directory.Exists(path))
            {
                var names = OperatingSystem.IsWindows() ? new[] { "java.exe", "javaw.exe" } : new[] { "java" };
                path = names.Select(n => Path.Combine(path, n)).Concat(names.Select(n => Path.Combine(path, "bin", n)))
                    .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException();
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!new[] { "java", "java.exe", "javaw.exe" }.Any(n => Path.GetFileName(path).Equals(n, comparison)) || !File.Exists(path)) throw new FileNotFoundException();
            SafePath.RejectLinks(path);
            // Preserve selection only. Normal Java discovery still probes the executable before launch.
            return path;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
        {
            warnings.Add("旧 Java 路径已失效、格式不受支持或无法安全读取，未自动选用；原设置保留，将沿用全局 Java 选择。");
            return null;
        }
    }
}
