using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Minecraft;

public static class GameSettingsPolicy
{
    public static GameLaunchOptions Options(GameInstance? instance, LauncherSettings settings)
        => instance?.LaunchOptions is { InheritGlobal: false } local ? local : settings.LaunchOptions ?? new();
    public static void Validate(GameLaunchOptions options)
    {
        if (options.WindowTitle is null || options.JvmArguments is null || options.GameArguments is null || options.BeforeLaunchExecutable is null || options.BeforeLaunchArguments is null || options.ClasspathPrepend is null) throw new ArgumentException("游戏设置包含无效的空值。");
        if (!Enum.IsDefined(options.Isolation) || !Enum.IsDefined(options.Visibility) || !Enum.IsDefined(options.Priority) || !Enum.IsDefined(options.IpPreference)) throw new ArgumentException("未知游戏设置选项。");
        if (options.WindowTitle.Length > 160 || options.WindowTitle.Any(char.IsControl)) throw new ArgumentException("游戏窗口标题须为 160 字以内的单行文字。");
        _ = JvmArguments(options);
        _ = GameArguments(options.GameArguments);
        if (options.WindowWidth.HasValue != options.WindowHeight.HasValue || options.WindowWidth is < 320 or > 16384 || options.WindowHeight is < 320 or > 16384 || options.MinimumMemoryMiB is < 512 or > 32768) throw new ArgumentException("实例窗口大小或初始内存无效。");
        if (options.CustomInfo is { } info && (info.Length > 256 || info.Any(char.IsControl))) throw new ArgumentException("自定义信息须为 256 字以内的单行文字。");
        if (options.BeforeLaunchExecutable.Length > 0 && !Path.IsPathFullyQualified(options.BeforeLaunchExecutable)) throw new ArgumentException("启动前程序须使用完整路径；不经过命令行 Shell 执行。");
        if (options.BeforeLaunchArguments.Length > 4096 || options.BeforeLaunchArguments.Any(c => c == '\0')) throw new ArgumentException("启动前程序参数过长或无效。");
        _ = LaunchPlanBuilder.TokenizeLegacy(options.BeforeLaunchArguments);
        _ = Classpath(options.ClasspathPrepend, false);
    }
    public static IReadOnlyList<string> GameArguments(string text)
    {
        if (text.Length > 4096 || text.Contains('\0')) throw new ArgumentException("游戏参数过长或无效。");
        var arguments = LaunchPlanBuilder.TokenizeLegacy(text);
        return ValidateGameArguments(arguments);
    }
    public static IReadOnlyList<string> ValidateGameArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(a => a is null || a.Length > 4096 || a.Contains('\0'))) throw new ArgumentException("游戏参数无效。");
        for (var i = 0; i < arguments.Count; i++)
        {
            var name = arguments[i]; if (name == "--fullscreen") continue;
            if (name is not ("--server" or "--port" or "--quickPlayMultiplayer" or "--quickPlaySingleplayer") || ++i >= arguments.Count || arguments[i].StartsWith('-')) throw new ArgumentException("游戏参数支持服务器地址、端口及快速游戏；账户、目录与运行环境由启动器管理。");
            if (name == "--port" && (!int.TryParse(arguments[i], out var port) || port is < 1 or > 65535)) throw new ArgumentException("服务器端口无效。");
        }
        return arguments;
    }
    public static IReadOnlyList<string> JvmArguments(GameLaunchOptions options)
    {
        if (options.JvmArguments.Length > 4096) throw new ArgumentException("JVM 参数过长。");
        var arguments = LaunchPlanBuilder.TokenizeLegacy(options.JvmArguments).ToList();
        foreach (var argument in arguments)
        {
            // Each line is a single JVM argument. Agents, classpath, credentials, TLS and bootstrap properties stay owned by the protected pipeline.
            if (!Regex.IsMatch(argument, @"^(-XX:[+-](UseG1GC|UseZGC|UseParallelGC|UseSerialGC|AlwaysPreTouch|UseStringDeduplication|UseAdaptiveSizePolicy|OmitStackTraceInFastThrow)|-XX:(MaxGCPauseMillis|G1HeapRegionSize|G1ReservePercent|InitiatingHeapOccupancyPercent|ParallelGCThreads|ConcGCThreads|SoftMaxHeapSize)=[0-9]+[kKmMgG]?|-D(file.encoding|user.language|user.country|sun.java2d.uiScale)=[A-Za-z0-9_.-]+|-Dlog4j2.formatMsgNoLookups=true)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ArgumentException("JVM 参数不受支持。可设置 GC 调优、字符编码和语言；每行一个参数。账户、证书、classpath 与引导参数由启动器管理。");
        }
        if (options.IpPreference == GameIpPreference.IPv4) { arguments.Add("-Djava.net.preferIPv4Stack=true"); arguments.Add("-Djava.net.preferIPv6Addresses=false"); }
        if (options.IpPreference == GameIpPreference.IPv6) { arguments.Add("-Djava.net.preferIPv4Stack=false"); arguments.Add("-Djava.net.preferIPv6Addresses=true"); }
        return arguments;
    }
    public static IReadOnlyList<string> Classpath(string text, bool inspectFiles = true)
    {
        if (text.Length > 16384) throw new ArgumentException("Classpath 列表过长。");
        var files = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (files.Length > 16 || files.Any(p => !Path.IsPathFullyQualified(p) || p.Any(char.IsControl) || !Path.GetExtension(p).Equals(".jar", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Classpath 每行填写一个完整的本机 JAR 路径，最多 16 项。");
        if (inspectFiles) foreach (var file in files)
        {
            IO.SafePath.RejectLinks(file);
            if (!File.Exists(file) || new FileInfo(file).Length > 256L * 1024 * 1024) throw new IOException("附加 Classpath JAR 不存在或超过大小限制。");
            using var archive = System.IO.Compression.ZipFile.OpenRead(file); if (archive.Entries.Count == 0) throw new InvalidDataException("附加 Classpath JAR 为空。");
        }
        return files.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }
    public static int MemoryMiB(GameInstance instance, LauncherSettings settings, MemoryStatus? memory, RuntimeArchitecture architecture = RuntimeArchitecture.X64)
    {
        var options = Options(instance, settings);
        var value = instance.MemoryMiB ?? settings.MemoryMiB;
        // The memory-follow switch is independent of the other per-instance launch overrides.
        if (instance.MemoryMiB is null && settings.LaunchOptions?.AutomaticMemory == true && memory is { TotalBytes: > 0, AvailableBytes: > 0 })
        {
            var available = memory.AvailableBytes / 1048576;
            var total = memory.TotalBytes / 1048576;
            value = (int)Math.Clamp(Math.Min(available * .6, total * .5) / 256, 2, 128) * 256;
        }
        if (architecture == RuntimeArchitecture.X86) value = Math.Min(value, 1536);
        var minimum = options.MinimumMemoryMiB ?? settings.MinimumMemoryMiB ?? 512;
        if (architecture == RuntimeArchitecture.X86 && minimum > 1536) throw new ArgumentException("32 位 Java 的初始内存不得超过 1536 MiB。");
        return Math.Max(minimum, value);
    }
    public static string Title(string template, GameInstance instance) => template.Replace("{name}", instance.Name, StringComparison.Ordinal).Replace("{version}", instance.GameVersionId ?? instance.VersionId, StringComparison.Ordinal);
}
