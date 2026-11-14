using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record RuleContext(string OSName, string OSVersion, RuntimeArchitecture JavaArchitecture,
    IReadOnlyDictionary<string, bool>? Features = null)
{
    public string ClasspathSeparator => OSName == "windows" ? ";" : ":";
    public string ArchitectureName => JavaArchitecture switch { RuntimeArchitecture.Arm64 => "aarch64", RuntimeArchitecture.X64 => "x86_64", RuntimeArchitecture.X86 => "x86", _ => "unknown" };
}
public static class MinecraftRuleEvaluator
{
    public static bool Allows(JsonObject item, RuleContext context)
    {
        if (!item.TryGetPropertyValue("rules", out var node)) return true;
        if (node is not JsonArray rules) throw new InvalidDataException("rules must be an array.");
        var allowed = false;
        foreach (var ruleNode in rules)
        {
            if (ruleNode is not JsonObject rule) throw new InvalidDataException("Invalid rule.");
            if (!Matches(rule, context)) continue;
            allowed = (string?)rule["action"] switch { "allow" => true, "disallow" => false, _ => throw new InvalidDataException("Unknown rule action.") };
        }
        return allowed;
    }
    private static bool Matches(JsonObject rule, RuleContext context)
    {
        if (rule["os"] is JsonObject os)
        {
            if ((string?)os["name"] is { } name && name != context.OSName) return false;
            if ((string?)os["arch"] is { } arch && !ArchMatches(arch, context.JavaArchitecture)) return false;
            if ((string?)os["version"] is { } version && !Regex.IsMatch(context.OSVersion, version, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        }
        if (rule["features"] is JsonObject features)
            foreach (var pair in features)
            {
                if (pair.Value is not JsonValue value || !value.TryGetValue<bool>(out var expected)) throw new InvalidDataException("Feature rule is not boolean.");
                var actual = context.Features?.GetValueOrDefault(pair.Key) ?? false;
                if (actual != expected) return false;
            }
        return true;
    }
    public static bool ArchMatches(string arch, RuntimeArchitecture value) => arch.ToLowerInvariant() switch
    {
        "aarch64" or "arm64" => value == RuntimeArchitecture.Arm64,
        "x86_64" or "amd64" or "x64" => value == RuntimeArchitecture.X64,
        "x86" or "i386" or "i686" => value == RuntimeArchitecture.X86,
        _ => Regex.IsMatch(value switch { RuntimeArchitecture.Arm64 => "aarch64", RuntimeArchitecture.X64 => "x86_64", RuntimeArchitecture.X86 => "x86", _ => "unknown" }, arch, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
    };
}
