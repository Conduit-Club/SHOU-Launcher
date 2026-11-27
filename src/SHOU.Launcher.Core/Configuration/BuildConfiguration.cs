using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Configuration;

public sealed partial class BuildConfiguration : IBuildConfiguration
{
    private static string? Read(string name)
    {
        if (EmbeddedValues.TryGetValue(name, out var value)) return value;
#if DEBUG
        if (name != "GITHUB_SHA") return Environment.GetEnvironmentVariable("PCL_" + name);
#endif
        return null;
    }
    public string? MicrosoftClientId => Read("MS_CLIENT_ID");
    public string? CurseForgeApiKey => Read("CURSEFORGE_API_KEY");
    public string SourceSha => EmbeddedValues.GetValueOrDefault("GITHUB_SHA") ?? "unconfigured";
    public string OwnerAssembly => typeof(BuildConfiguration).Assembly.GetName().Name!;
    public string Configuration =>
#if DEBUG
        "Debug";
#elif CI
        "CI";
#elif BETA
        "Beta";
#else
        "Release";
#endif
    public BuildIdentity GetIdentity()
    {
        using var stream = typeof(BuildConfiguration).Assembly.GetManifestResourceStream("PCL.metadata.json")
            ?? throw new InvalidOperationException("Required PCL.metadata.json resource is missing.");
        using var metadata = JsonDocument.Parse(stream);
        return new(metadata.RootElement.GetProperty("name").GetString()!,
            metadata.RootElement.GetProperty("version").GetProperty("base").GetString()!, metadata.RootElement.GetProperty("codename").GetString()!, SourceSha,
            Configuration, OwnerAssembly, !string.IsNullOrWhiteSpace(MicrosoftClientId), !string.IsNullOrWhiteSpace(CurseForgeApiKey));
    }
}
