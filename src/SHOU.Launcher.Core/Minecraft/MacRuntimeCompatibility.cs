using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Minecraft;

public sealed record RuntimeLibraryProfile(JsonObject Metadata, string? Id);

/// <summary>Launcher-owned macOS rules applied after loader merge. Original metadata and other platforms are untouched.</summary>
public static class MacRuntimeCompatibility
{
    public const string Revision = "macos-arm64-runtime-r35-v1";
    // Artifact identities verified against Prism meta 9cac2ca1cf410146d3a04fbd33752820fefdd70e and the publishers.
    private static readonly HashSet<string> LegacyVersions = ["2.9.1", "2.9.1-nightly-20130708-debug3", "2.9.1-nightly-20131017", "2.9.1-nightly-20131120", "2.9.2-nightly-20140822", "2.9.3", "2.9.4-nightly-20150209"];
    public static bool UsesLwjgl2(JsonObject metadata, RuleContext context) => (metadata["libraries"] as JsonArray)?.OfType<JsonObject>()
        .Any(l => ((string?)l["name"])?.StartsWith("org.lwjgl.lwjgl:lwjgl:", StringComparison.Ordinal) == true && MinecraftRuleEvaluator.Allows(l, context)) == true;

    public static RuntimeLibraryProfile Resolve(JsonObject metadata, RuleContext context)
    {
        if (context.OSName != "osx" || context.JavaArchitecture != RuntimeArchitecture.Arm64 || metadata["libraries"] is not JsonArray libraries)
            return new(metadata, null);
        var replacements = new JsonArray(); var changed = false;
        foreach (var node in libraries)
        {
            if (node is not JsonObject library || !MinecraftRuleEvaluator.Allows(library, context)) { replacements.Add(node?.DeepClone()); continue; }
            var coordinate = (string?)library["name"] ?? ""; var parts = coordinate.Split(':'); JsonObject? replacement = null;
            if (parts.Length == 3 && parts[0] == "org.lwjgl.lwjgl" && LegacyVersions.Contains(parts[2]))
            {
                if (parts[1] == "lwjgl") replacement = Artifact("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209", "697517568c68e78ae0b4544145af031c81082dfe", 1047168);
                else if (parts[1] == "lwjgl_util") replacement = Artifact("org.lwjgl.lwjgl:lwjgl_util:2.9.4-nightly-20150209", "d51a7c040a721d13efdfbd34f8b257b2df882ad0", 173887);
                else if (parts[1] == "lwjgl-platform") replacement = Native("org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209", "eff546c0b319d6ffc7a835652124c18089c67f36", 488316,
                    "https://github.com/MinecraftMachina/lwjgl/releases/download/2.9.4-20150209-mmachina.2/lwjgl-platform-2.9.4-nightly-20150209-natives-osx.jar");
            }
            else if (coordinate == "net.java.jinput:jinput-platform:2.0.5") replacement = Native(coordinate, "5189eb40db3087fb11ca063b68fa4f4c20b199dd", 10031,
                "https://raw.githubusercontent.com/r58Playz/jinput-m1/8b2c3a10b4853923efc92f29cbad8e0f304dbd6d/plugins/OSX/bin/jinput-platform-2.0.5.jar");
            else if (coordinate == "ca.weblite:java-objc-bridge:1.0.0") replacement = Artifact("ca.weblite:java-objc-bridge:1.1.0-mmachina.1", "369a83621e3c65496348491e533cb97fe5f2f37d", 91947,
                "https://github.com/MinecraftMachina/Java-Objective-C-Bridge/releases/download/1.1.0-mmachina.1/java-objc-bridge-1.1.jar");
            else if (parts.Length == 3 && parts[0] == "com.mojang" && parts[1] == "text2speech" && parts[2] is "1.0.10" or "1.5" or "1.6" or "1.7" or "1.10.1" or "1.10.3" or "1.11.2")
                replacement = Artifact("com.mojang:text2speech:1.11.3", "f378f889797edd7df8d32272c06ca80a1b6b0f58", 13164);
            else if (parts.Length == 3 && parts[0] == "net.java.dev.jna" && parts[2] is "3.4.0" or "4.4.0" or "5.6.0" or "5.8.0" or "5.9.0" or "5.10.0" or "5.12.1")
            {
                if (parts[1] == "jna") replacement = Artifact("net.java.dev.jna:jna:5.13.0", "1200e7ebeedbe0d10062093f32925a912020e747", 1879325);
                else if (parts[1] is "platform" or "jna-platform") replacement = Artifact("net.java.dev.jna:jna-platform:5.13.0", "88e9a306715e9379f3122415ef4ae759a352640d", 1363209);
            }
            changed |= replacement is not null; replacements.Add(replacement ?? library.DeepClone());
        }
        if (!changed) return new(metadata, null);
        var patched = (JsonObject)metadata.DeepClone(); patched["libraries"] = replacements;
        return new(patched, Revision);
    }
    private static JsonObject Artifact(string name, string sha1, long size, string? url = null)
    {
        var path = LibraryResolver.MavenPath(name);
        return new() { ["name"] = name, ["downloads"] = new JsonObject { ["artifact"] = Descriptor(path, url ?? "https://libraries.minecraft.net/" + path, sha1, size) } };
    }
    private static JsonObject Native(string name, string sha1, long size, string url)
    {
        const string classifier = "natives-osx-arm64";
        return new() { ["name"] = name, ["natives"] = new JsonObject { ["osx"] = classifier },
            ["extract"] = new JsonObject { ["exclude"] = new JsonArray("META-INF/") },
            ["downloads"] = new JsonObject { ["classifiers"] = new JsonObject { [classifier] = Descriptor(LibraryResolver.MavenPath(name + ":" + classifier), url, sha1, size) } } };
    }
    private static JsonObject Descriptor(string path, string url, string sha1, long size) => new() { ["path"] = path, ["url"] = url, ["sha1"] = sha1, ["size"] = size };
}
