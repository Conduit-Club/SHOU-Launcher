using System.ComponentModel;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Java;

/// <summary>Inspects an actual child JVM; neither executable headers nor JDK-only files establish usability.</summary>
public sealed partial class JavaRuntimeProbe(IProcessRunner processes, TimeSpan? timeout = null)
{
    public async Task<JavaInstallation> ProbeAsync(string executable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            return Failure(executable ?? "", "InvalidPath", "Java 必须使用绝对可执行文件路径。");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) return Failure(executable, "NotFound", "Java 可执行文件不存在或不可访问。");
        try
        {
            var result = await processes.RunAsync(new ProcessSpec(executable,
                ["-XshowSettings:properties", "-version"], Path.GetDirectoryName(executable)!,
                Timeout: timeout ?? TimeSpan.FromSeconds(10), OutputLimitCharacters: 131072), cancellationToken: cancellationToken);
            if (result.Cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Failure(executable, "Cancelled", "Java 探测已取消。");
            }
            if (result.TimedOut) return Failure(executable, "TimedOut", "Java 探测超时，已结束本次进程。");
            if (result.OutputTruncated) return Failure(executable, "OutputLimitExceeded", "Java 探测输出超过限制或未完整读取。");
            if (result.ExitCode != 0) return Failure(executable, "FailedExit", $"Java 探测退出码为 {result.ExitCode}。");
            return ParseOutput(executable, result.StandardOutput, result.StandardError);
        }
        catch (Win32Exception ex)
        {
            var code = ex.NativeErrorCode switch
            {
                2 or 3 => "NotFound",
                5 or 13 => "NotExecutable",
                8 or 86 or 193 or 216 => "BadExecutableOrArchitecture",
                _ => "StartFailed"
            };
            return Failure(executable, code, $"操作系统无法执行该 Java（错误 {ex.NativeErrorCode}）。");
        }
        catch (UnauthorizedAccessException) { return Failure(executable, "NotExecutable", "没有执行该 Java 的权限。"); }
        catch (IOException) { return Failure(executable, "IOFailure", "读取或执行 Java 时发生文件错误。"); }
    }

    public static JavaInstallation ParseOutput(string executable, string standardOutput, string standardError)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (standardOutput + "\n" + standardError).Split('\n'))
        {
            var match = PropertyLine().Match(line.TrimEnd('\r'));
            if (match.Success) properties[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }
        var fullVersion = properties.GetValueOrDefault("java.runtime.version") ?? properties.GetValueOrDefault("java.version");
        var home = properties.GetValueOrDefault("java.home");
        var architecture = NormalizeArchitecture(properties.GetValueOrDefault("os.arch"));
        var version = MajorVersion().Match(properties.GetValueOrDefault("java.version") ?? fullVersion ?? "");
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(fullVersion) || !version.Success)
            return Failure(executable, "InvalidOutput", "Java 未返回有效的版本与 java.home 属性。");
        var major = int.TryParse(version.Groups[1].Value == "1" ? version.Groups[2].Value : version.Groups[1].Value, out var number) ? number : 0;
        if (major < 1) return Failure(executable, "InvalidOutput", "无法识别 Java 主版本。");
        if (architecture == RuntimeArchitecture.Unknown)
            return Failure(executable, "UnknownArchitecture", "Java 返回了不支持或缺失的 os.arch 属性。");
        return new JavaInstallation(executable, home, fullVersion, major,
            properties.GetValueOrDefault("java.vendor") ?? properties.GetValueOrDefault("java.vm.vendor") ?? "Unknown",
            architecture, "probe");
    }

    public static RuntimeArchitecture NormalizeArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "aarch64" or "arm64" => RuntimeArchitecture.Arm64,
        "x86_64" or "amd64" or "x64" => RuntimeArchitecture.X64,
        "x86" or "i386" or "i486" or "i586" or "i686" => RuntimeArchitecture.X86,
        _ => RuntimeArchitecture.Unknown
    };

    private static JavaInstallation Failure(string executable, string code, string message) =>
        new(executable, "", "", 0, "", RuntimeArchitecture.Unknown, "probe", $"[{code}] {message}");

    [GeneratedRegex(@"^\s*(java\.home|java\.version|java\.runtime\.version|java\.vendor|java\.vm\.vendor|os\.arch)\s*=\s*(.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyLine();
    [GeneratedRegex(@"^(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex MajorVersion();
}
