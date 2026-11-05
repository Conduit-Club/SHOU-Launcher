using System.IO.Compression;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record LibraryFile(string Name, string RelativePath, Uri Url, string? Sha1, long? Size,
    bool LegacyNative = false, IReadOnlyList<string>? Excludes = null, bool Generated = false);
public sealed record LibraryResolution(IReadOnlyList<LibraryFile> Files, IReadOnlyList<string> ClasspathRelativePaths);

public static class LibraryResolver
{
    public static LibraryResolution Resolve(JsonObject metadata, RuleContext context)
    {
        var files = new List<LibraryFile>();
        var classpath = new List<string>();
        if (metadata["libraries"] is not JsonArray libraries) throw new InvalidDataException("Version has no library list.");
        foreach (var value in libraries)
        {
            var lib = value as JsonObject ?? throw new InvalidDataException("Invalid library descriptor.");
            if (!MinecraftRuleEvaluator.Allows(lib, context)) continue;
            var name = (string?)lib["name"] ?? throw new InvalidDataException("Library name missing.");
            var downloads = lib["downloads"] as JsonObject;
            if (lib["natives"] is JsonObject declaredNatives && declaredNatives[context.OSName] is null)
                throw new PlatformNotSupportedException($"{name} does not declare native support for {context.OSName}.");
            if (downloads?["artifact"] is JsonObject artifact)
            {
                var file = ParseFile(name, artifact, false);
                files.Add(file); classpath.Add(file.RelativePath);
                // Modern native JARs are ordinary artifacts and must remain in classpath.
            }
            else if (downloads is null && lib["natives"] is null)
            {
                var path = MavenPath(name);
                var url = new Uri(((string?)lib["url"] ?? "https://libraries.minecraft.net/").TrimEnd('/') + "/" + path);
                files.Add(new(name, path, url, (string?)lib["sha1"], (long?)lib["size"])); classpath.Add(path);
            }
            if (lib["natives"] is JsonObject natives && (string?)natives[context.OSName] is { } template)
            {
                var classifier = template.Replace("${arch}", context.JavaArchitecture == RuntimeArchitecture.X86 ? "32" : "64", StringComparison.Ordinal);
                if (context.JavaArchitecture == RuntimeArchitecture.Arm64 && !classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase) && !classifier.Contains("aarch64", StringComparison.OrdinalIgnoreCase))
                    throw new PlatformNotSupportedException($"Legacy native {name}/{classifier} has no verified ARM64 slice; x64 compatibility is not enabled.");
                var native = downloads?["classifiers"]?[classifier] as JsonObject
                    ?? throw new PlatformNotSupportedException($"{name} does not provide native classifier {classifier}.");
                var excludes = (lib["extract"]?["exclude"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];
                files.Add(ParseFile(name + ":" + classifier, native, true) with { Excludes = excludes });
            }
        }
        return new(files.DistinctBy(f => (f.RelativePath, f.LegacyNative)).ToArray(), classpath.Distinct(StringComparer.Ordinal).ToArray());
    }
    private static LibraryFile ParseFile(string name, JsonObject file, bool native)
    {
        var path = (string?)file["path"] ?? MavenPath(name);
        var url = (string?)file["url"] ?? throw new InvalidDataException("Library download URL missing.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Invalid library URL.");
        return new(name, path, uri, (string?)file["sha1"], (long?)file["size"], native, Generated: (bool?)file["shouGenerated"] ?? false);
    }
    public static string MavenPath(string coordinate)
    {
        var extensionSplit = coordinate.Split('@', 2);
        var p = extensionSplit[0].Split(':');
        if (p.Length is < 3 or > 4 || p.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid Maven coordinate.");
        var suffix = p.Length == 4 ? "-" + p[3] : "";
        return p[0].Replace('.', '/') + "/" + p[1] + "/" + p[2] + "/" + p[1] + "-" + p[2] + suffix + "." + (extensionSplit.Length == 2 ? extensionSplit[1] : "jar");
    }
    public static async Task<IReadOnlyList<string>> ExtractLegacyNativesAsync(string archivePath, string destination,
        IReadOnlyList<string>? excludes = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var extracted = new List<string>(); long total = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.FullName.Replace('\\', '/');
            if ((excludes ?? []).Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))) continue;
            var target = SafePath.Within(destination, path.TrimEnd('/'));
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind == 0xa000) throw new InvalidDataException("Native archive symbolic links are not accepted.");
            if (path.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            total = checked(total + entry.Length);
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("Native archive exceeds extraction budget.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                var permissions = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1ff);
                File.SetUnixFileMode(target, permissions == 0 ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : permissions);
            }
            extracted.Add(target);
        }
        return extracted;
    }
}
