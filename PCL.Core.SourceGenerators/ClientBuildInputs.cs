using System;
using System.Collections.Generic;

namespace SHOU.Build.Generation;

// Shared by both hosts: presence of the switch enables only these three distributable inputs.
internal static class ClientBuildInputs
{
    internal static Dictionary<string, string> Capture()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
#pragma warning disable RS1035
        var environment = Environment.GetEnvironmentVariables();
#pragma warning restore RS1035
        if (!environment.Contains("PCL_WRITE_SECRET")) return result;
        foreach (var name in new[] { "MS_CLIENT_ID", "CURSEFORGE_API_KEY", "GITHUB_SHA" })
        {
            var value = environment["PCL_" + name]?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(name, value!);
        }
        return result;
    }
    internal static string Literal(string value) => "@\"" + value.Replace("\"", "\"\"") + "\"";
}
