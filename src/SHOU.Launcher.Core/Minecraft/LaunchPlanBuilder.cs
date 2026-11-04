using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record LaunchIdentity(string PlayerName, string Uuid, string AccessToken, string UserType = "legacy",
    string? Xuid = null, string? ClientId = null, Uri? AuthenticationServer = null, string? InjectorPath = null,
    string? PrefetchedMetadataBase64 = null);
public sealed record LaunchInputs(ResolvedVersion Version, JavaInstallation Java, RuleContext Context,
    LaunchIdentity Identity, string GameDirectory, string LibrariesDirectory, string AssetsDirectory,
    string NativesDirectory, string ClientJar, string AssetIndexId, int MemoryMiB,
    IReadOnlyList<string>? LegacyExtractions = null, IReadOnlyList<string>? ExtraJvmArguments = null, string? TemporaryDirectory = null);

public static class LaunchPlanBuilder
{
    public static LaunchPlan Build(LaunchInputs input)
    {
        if (!input.Java.IsUsable) throw new InvalidOperationException("Selected Java did not pass probe.");
        if (input.Java.MajorVersion != input.Version.RequiredJavaMajor) throw new InvalidOperationException($"Version metadata requires Java {input.Version.RequiredJavaMajor}; selected Java is {input.Java.MajorVersion}.");
        if (input.Context.JavaArchitecture != input.Java.RuntimeArchitecture) throw new InvalidOperationException("Native rule architecture differs from actual selected Java.");
        if (input.MemoryMiB is < 512 or > 32768) throw new ArgumentOutOfRangeException(nameof(input.MemoryMiB));
        var resolved = LibraryResolver.Resolve(input.Version.Metadata, input.Context);
        var classpath = resolved.ClasspathRelativePaths.Select(p => SafePath.Within(input.LibrariesDirectory, p)).Append(Path.GetFullPath(input.ClientJar)).ToArray();
        var mainClass = (string?)input.Version.Metadata["mainClass"] ?? throw new InvalidDataException("Version mainClass is missing.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = input.Identity.PlayerName, ["auth_uuid"] = input.Identity.Uuid,
            ["auth_access_token"] = input.Identity.AccessToken, ["auth_session"] = input.Identity.AccessToken,
            ["user_type"] = input.Identity.UserType, ["user_properties"] = "{}",
            ["version_name"] = input.Version.Id, ["version_type"] = (string?)input.Version.Metadata["type"] ?? "release",
            ["game_directory"] = Path.GetFullPath(input.GameDirectory), ["assets_root"] = Path.GetFullPath(input.AssetsDirectory),
            ["game_assets"] = Path.Combine(input.AssetsDirectory, "virtual", input.AssetIndexId), ["assets_index_name"] = input.AssetIndexId,
            ["natives_directory"] = Path.GetFullPath(input.NativesDirectory), ["library_directory"] = Path.GetFullPath(input.LibrariesDirectory),
            ["classpath"] = string.Join(input.Context.ClasspathSeparator, classpath), ["classpath_separator"] = input.Context.ClasspathSeparator,
            ["launcher_name"] = "SHOU-Launcher", ["launcher_version"] = "1.5.0", ["clientid"] = input.Identity.ClientId ?? "",
            ["auth_xuid"] = input.Identity.Xuid ?? "", ["resolution_width"] = "960", ["resolution_height"] = "600"
        };
        string Expand(string argument) => Regex.Replace(argument, @"\$\{([^}]+)\}", match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new InvalidDataException("Unknown launch argument placeholder: " + match.Groups[1].Value), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var jvm = input.Version.Metadata["arguments"]?["jvm"] is JsonArray modernJvm
            ? ExpandArguments(modernJvm, input.Context).Select(Expand).ToList()
            : new List<string> { "-Djava.library.path=" + input.NativesDirectory, "-cp", values["classpath"] };
        if (input.Context.OSName == "osx" && !jvm.Contains("-XstartOnFirstThread", StringComparer.Ordinal)) jvm.Add("-XstartOnFirstThread");
        if (jvm.Count(a => a == "-XstartOnFirstThread") > 1) throw new InvalidDataException("Duplicate -XstartOnFirstThread.");
        if (!jvm.Any(a => a.StartsWith("-Xmx", StringComparison.Ordinal))) jvm.Add("-Xmx" + input.MemoryMiB + "M");
        if (input.ExtraJvmArguments is { } extras)
        {
            if (extras.Any(a => a is "-cp" or "-classpath" || a.StartsWith("-javaagent:", StringComparison.Ordinal)))
                throw new InvalidDataException("Classpath and Java agents must be configured by the trusted launch pipeline.");
            jvm.AddRange(extras);
        }
        var environment = new Dictionary<string, string?> { ["JAVA_HOME"] = input.Java.JavaHome };
        if (input.TemporaryDirectory is { } temporary)
        {
            temporary = Path.GetFullPath(temporary);
            jvm.RemoveAll(a => a.StartsWith("-Djava.io.tmpdir=", StringComparison.Ordinal));
            jvm.Add("-Djava.io.tmpdir=" + temporary);
            environment["TMPDIR"] = environment["TMP"] = environment["TEMP"] = temporary;
        }
        if (input.Identity.AuthenticationServer is { } server)
        {
            if (string.IsNullOrWhiteSpace(input.Identity.InjectorPath) || string.IsNullOrWhiteSpace(input.Identity.PrefetchedMetadataBase64))
                throw new InvalidOperationException("Third-party account requires verified authlib-injector and prefetched server metadata.");
            jvm.Insert(0, "-javaagent:" + Path.GetFullPath(input.Identity.InjectorPath) + "=" + server.AbsoluteUri.TrimEnd('/'));
            jvm.Add("-Dauthlibinjector.side=client");
            jvm.Add("-Dauthlibinjector.yggdrasil.prefetched=" + input.Identity.PrefetchedMetadataBase64);
            if (input.Context.OSName == "windows") jvm.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT");
        }
        if (input.Context.OSName != "windows" && jvm.Any(a => a.Contains("WINDOWS-ROOT", StringComparison.Ordinal))) throw new InvalidDataException("Windows truststore configuration entered a non-Windows plan.");
        var game = input.Version.Metadata["arguments"]?["game"] is JsonArray modernGame
            ? ExpandArguments(modernGame, input.Context).Select(Expand).ToArray()
            : TokenizeLegacy((string?)input.Version.Metadata["minecraftArguments"] ?? throw new InvalidDataException("Version game arguments are missing.")).Select(Expand).ToArray();
        var argv = jvm.Append(mainClass).Concat(game).ToArray();
        var diagnostics = new List<string>
        {
            $"Minecraft={input.Version.Id}; JavaMajor={input.Java.MajorVersion}; JavaArch={input.Java.RuntimeArchitecture}; OS={input.Context.OSName}",
            $"ClasspathEntries={classpath.Length}; LegacyNativeArchives={resolved.Files.Count(f => f.LegacyNative)}; ArgvCount={argv.Length}",
            "Arguments and environment are omitted from this report; account credentials are runtime-only."
        };
        // Do not copy argv into diagnostics. Extra JVM arguments may contain secrets unknown to identity redaction.
        return new(input.Java.ExecutablePath, input.Java.RuntimeArchitecture, Path.GetFullPath(input.GameDirectory), argv,
            environment, classpath,
            resolved.Files.Select(f => SafePath.Within(input.LibrariesDirectory, f.RelativePath)).ToArray(), input.LegacyExtractions ?? [], diagnostics);
    }
    private static string RedactArgument(string argument, LaunchIdentity identity)
    {
        var value = argument;
        foreach (var secret in new[] { identity.AccessToken, identity.Uuid, identity.Xuid, identity.ClientId, identity.PlayerName, identity.PrefetchedMetadataBase64 }.Where(s => !string.IsNullOrEmpty(s)))
            value = value.Replace(secret!, "<redacted>", StringComparison.Ordinal);
        return value;
    }
    public static IReadOnlyList<string> ExpandArguments(JsonArray arguments, RuleContext context)
    {
        var result = new List<string>();
        foreach (var node in arguments)
        {
            if (node is JsonValue value) result.Add(value.GetValue<string>());
            else if (node is JsonObject conditional && MinecraftRuleEvaluator.Allows(conditional, context))
            {
                if (conditional["value"] is JsonArray values) result.AddRange(values.Select(v => v?.GetValue<string>() ?? throw new InvalidDataException("Null argument value.")));
                else if (conditional["value"] is JsonValue single) result.Add(single.GetValue<string>());
                else throw new InvalidDataException("Conditional argument has no value.");
            }
        }
        return result;
    }
    public static IReadOnlyList<string> TokenizeLegacy(string value)
    {
        // Only the historical metadata format is tokenized; process argv is never joined/split.
        var result = new List<string>(); var current = new StringBuilder(); char? quote = null; var started = false;
        foreach (var ch in value)
        {
            if (quote is { } delimiter) { if (ch == delimiter) quote = null; else current.Append(ch); started = true; }
            else if (ch is '\'' or '"') { quote = ch; started = true; }
            else if (char.IsWhiteSpace(ch)) { if (started) { result.Add(current.ToString()); current.Clear(); started = false; } }
            else { current.Append(ch); started = true; }
        }
        if (quote is not null) throw new InvalidDataException("Unterminated quoted legacy metadata argument.");
        if (started) result.Add(current.ToString());
        return result;
    }
}
