using System.Reflection;
using System.Security.Cryptography;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop;

public static partial class Bootstrap
{
    private const string JavaResourceName = "SHOU.Launcher.Desktop.JavaBootstrap.jar";
    private static JavaBootstrapArtifact? productJavaBootstrap;
    private static JavaBootstrapArtifact? DescribeJavaBootstrap(IAppPaths paths)
    {
        var assembly = typeof(Bootstrap).Assembly;
        var hash = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SHOU.JavaBootstrap.Sha256")?.Value;
        var exists = assembly.GetManifestResourceNames().Contains(JavaResourceName, StringComparer.Ordinal);
        if (hash is null && !exists) return null; // Direct developer builds cannot launch real accounts.
        if (hash?.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || !exists) throw new InvalidDataException("Trusted Java bootstrap resource identity is incomplete.");
        return new(SafePath.Within(paths.CacheDirectory, "launcher-bootstrap/" + hash.ToLowerInvariant() + "/bootstrap.jar"), hash.ToLowerInvariant());
    }
    private static async Task InstallJavaBootstrapAsync(JavaBootstrapArtifact artifact)
    {
        await using var input = typeof(Bootstrap).Assembly.GetManifestResourceStream(JavaResourceName)
            ?? throw new InvalidDataException("Trusted Java bootstrap resource is missing.");
        if (input.Length > 2 * 1024 * 1024) throw new InvalidDataException("Trusted Java bootstrap resource exceeds its budget.");
        using var buffer = new MemoryStream(); await input.CopyToAsync(buffer).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(artifact.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Trusted Java bootstrap resource checksum mismatch.");
        if (File.Exists(artifact.JarPath))
        {
            await using var old = File.OpenRead(artifact.JarPath);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(old).ConfigureAwait(false)) == artifact.Sha256) return;
        }
        var directory = Path.GetDirectoryName(artifact.JarPath)!; Directory.CreateDirectory(directory);
        var temporary = SafePath.Within(directory, "bootstrap.tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await file.WriteAsync(bytes).ConfigureAwait(false); await file.FlushAsync().ConfigureAwait(false); }
            File.Move(temporary, artifact.JarPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
