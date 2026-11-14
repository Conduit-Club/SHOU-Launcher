namespace SHOU.Launcher.Core.Abstractions;

/// <summary>Public build artifact identity; contains no account or session values.</summary>
public sealed record JavaBootstrapArtifact(string JarPath, string Sha256);
