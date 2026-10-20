using System;

namespace PCL.Core.Minecraft.Skin;

/// <summary>
/// Normalizes profile identifiers for Yggdrasil session-server requests.
/// </summary>
public static class SkinProfileId
{
    /// <summary>
    /// Returns the unhyphenated UUID required by the session-server profile endpoint.
    /// Non-UUID identifiers are trimmed and preserved for compatible custom servers.
    /// </summary>
    public static string NormalizeForSession(string? profileId)
    {
        var value = profileId?.Trim() ?? string.Empty;
        return Guid.TryParse(value, out var uuid) ? uuid.ToString("N") : value;
    }
}
