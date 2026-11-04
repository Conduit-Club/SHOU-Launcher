using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Java;

public sealed class MojangJavaRuntimeInstaller(IVerifiedDownloader downloader, IJavaDiscovery discovery,
    string managedRuntimeRoot, string metadataCacheDirectory) : IJavaRuntimeInstaller
{
    public static readonly Uri OfficialCatalog = new("https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");
    public const string StateFileName = ".shou-runtime.json";

    public async Task<JavaInstallation> InstallAsync(JavaRuntimeRequest request, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var component = Identifier(request.Component);
        if (request.MajorVersion < 1) throw new ArgumentException("A metadata Java major is required.");
        if (!Path.IsPathFullyQualified(request.InstallRoot)) throw new ArgumentException("The runtime installation root must be absolute.");
        var root = Contained(managedRuntimeRoot, request.InstallRoot, allowAbsolute: true);
        var platform = PlatformKey(request.OS, request.Architecture);
        var catalogUri = HttpUri(request.CatalogUri ?? OfficialCatalog);
        Directory.CreateDirectory(root);
        var lockPath = Contained(root, ".locks/" + component + "-" + platform + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        RejectLink(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        cancellationToken.ThrowIfCancellationRequested();

        var catalogName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalogUri.AbsoluteUri))).ToLowerInvariant();
        var catalogPath = Contained(metadataCacheDirectory, catalogName + ".json");
        // Unhashed metadata is fetched fresh by IVerifiedDownloader and must pass the typed checks below.
        await downloader.DownloadAsync(new DownloadRequest([catalogUri], catalogPath, AllowResume: false), progress, cancellationToken);
        var catalog = await ReadObjectAsync(catalogPath, cancellationToken);
        if (catalog[platform]?[component] is not JsonArray candidates)
            throw new PlatformNotSupportedException($"Mojang 没有提供 {platform}/{component}。");
        var entry = candidates.OfType<JsonObject>().FirstOrDefault(candidate =>
            VersionMajor((string?)candidate["version"]?["name"]) == request.MajorVersion)
            ?? throw new PlatformNotSupportedException($"Mojang 没有提供 {platform}/{component} 的 Java {request.MajorVersion}。");
        var manifestDescriptor = entry["manifest"] as JsonObject ?? throw new InvalidDataException("Runtime manifest descriptor is missing.");
        var manifestSha1 = Hash((string?)manifestDescriptor["sha1"]);
        var manifestSize = (long?)manifestDescriptor["size"] ?? throw new InvalidDataException("Runtime manifest size is missing.");
        if (manifestSize is <= 0 or > 33554432) throw new InvalidDataException("Runtime manifest size exceeds its metadata budget.");
        var manifestUri = HttpUri(new Uri((string?)manifestDescriptor["url"] ?? throw new InvalidDataException("Runtime manifest URL is missing.")));
        var manifestPath = Contained(metadataCacheDirectory, "manifests/" + manifestSha1 + ".json");
        await downloader.DownloadAsync(new DownloadRequest([manifestUri], manifestPath, Sha1: manifestSha1, Size: manifestSize), progress, cancellationToken);
        var manifest = await ReadObjectAsync(manifestPath, cancellationToken);
        var files = manifest["files"] as JsonObject ?? throw new InvalidDataException("Runtime manifest has no files.");
        if (files.Count is 0 or > 100000) throw new InvalidDataException("Runtime file count is invalid.");
        var final = Contained(root, component + "/" + platform + "/" + manifestSha1);
        var javaName = request.OS == LauncherOS.Windows ? "java.exe" : "java";
        var javaEntry = files.FirstOrDefault(pair => pair.Value?["type"]?.GetValue<string>() == "file"
            && (pair.Key == "bin/" + javaName || pair.Key.EndsWith("/bin/" + javaName, StringComparison.Ordinal))).Key;
        if (javaEntry is null) throw new InvalidDataException("Runtime manifest does not contain a Java executable.");

        if (Directory.Exists(final))
        {
            await VerifyExistingAsync(final, files, cancellationToken);
            var existing = await ProbeRequiredAsync(Contained(final, javaEntry), request, cancellationToken);
            await WriteStateAsync(final, true, manifestSha1, javaEntry, cancellationToken);
            return existing with { Source = "managed:Mojang" };
        }

        var staging = Contained(root, ".staging/" + component + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        await WriteStateAsync(staging, false, manifestSha1, javaEntry, cancellationToken);
        long total = 0;
        var regularFiles = new List<(string Name, JsonObject Descriptor)>();
        var links = new List<(string Name, string Target)>();
        // Validate every manifest path and entry before downloading bytes.
        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Contained(staging, pair.Key);
            if (pair.Value is not JsonObject descriptor) throw new InvalidDataException("Invalid runtime file entry.");
            switch ((string?)descriptor["type"])
            {
                case "directory": Directory.CreateDirectory(path); break;
                case "file":
                    var raw = Raw(descriptor);
                    var size = (long?)raw["size"] ?? throw new InvalidDataException("Runtime file size missing.");
                    if (size < 0 || (total = checked(total + size)) > 4L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Runtime download exceeds its size budget.");
                    Hash((string?)raw["sha1"]);
                    HttpUri(new Uri((string?)raw["url"] ?? throw new InvalidDataException("Runtime file URL missing.")));
                    regularFiles.Add((pair.Key, descriptor));
                    break;
                case "link":
                    var target = (string?)descriptor["target"] ?? throw new InvalidDataException("Runtime link target missing.");
                    LinkTarget(staging, path, target);
                    links.Add((pair.Key, target));
                    break;
                default: throw new InvalidDataException("Unsupported runtime manifest entry type.");
            }
        }
        await Parallel.ForEachAsync(regularFiles, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                var raw = Raw(file.Descriptor);
                var path = Contained(staging, file.Name);
                await downloader.DownloadAsync(new DownloadRequest([HttpUri(new Uri(raw["url"]!.GetValue<string>()))], path,
                    Sha1: Hash((string?)raw["sha1"]), Size: (long?)raw["size"]), progress, token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                    (bool?)file.Descriptor["executable"] == true
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            });
        while (links.Count > 0)
        {
            var created = 0;
            foreach (var link in links.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Contained(staging, link.Name);
                var target = LinkTarget(staging, path, link.Target);
                if (!File.Exists(target) && !Directory.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (Directory.Exists(target)) Directory.CreateSymbolicLink(path, link.Target);
                else File.CreateSymbolicLink(path, link.Target);
                Contained(staging, link.Name); // Resolve the created link and recheck its boundary.
                links.Remove(link); created++;
            }
            if (created == 0) throw new InvalidDataException("Runtime contains dangling or cyclic links.");
        }
        progress?.Report(new TransferProgress(component, "Probing", total, total, platform));
        await ProbeRequiredAsync(Contained(staging, javaEntry), request, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        Directory.Move(staging, final);
        var installed = await ProbeRequiredAsync(Contained(final, javaEntry), request, cancellationToken);
        await WriteStateAsync(final, true, manifestSha1, javaEntry, cancellationToken);
        progress?.Report(new TransferProgress(component, "Completed", total, total, platform));
        return installed with { Source = "managed:Mojang" };
    }

    public static bool IsIncompleteRuntime(string directory)
    {
        var path = Path.Combine(directory, StateFileName);
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return !document.RootElement.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return true; }
    }

    private async Task<JavaInstallation> ProbeRequiredAsync(string executable, JavaRuntimeRequest request, CancellationToken ct)
    {
        var java = await discovery.ProbeAsync(executable, ct);
        if (!java.IsUsable || java.MajorVersion != request.MajorVersion || java.RuntimeArchitecture != request.Architecture)
            throw new InvalidDataException("安装后的 Java 未通过所需主版本与架构探测；没有登记为可用。" + (java.ProbeError ?? ""));
        return java;
    }

    private static async Task VerifyExistingAsync(string root, JsonObject files, CancellationToken ct)
    {
        foreach (var pair in files)
        {
            var path = Contained(root, pair.Key);
            if ((string?)pair.Value?["type"] == "file")
            {
                RejectLink(Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                var raw = Raw(pair.Value!.AsObject());
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != (long?)raw["size"]) throw new InvalidDataException("Existing runtime is incomplete; files were preserved.");
                await using var input = File.OpenRead(path);
                if (!Convert.ToHexString(await SHA1.HashDataAsync(input, ct)).Equals(Hash((string?)raw["sha1"]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Existing runtime hash mismatch; files were preserved.");
            }
            else if ((string?)pair.Value?["type"] == "link")
            {
                var originalPath = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(originalPath);
                if (info.LinkTarget != (string?)pair.Value?["target"]) throw new InvalidDataException("Existing runtime link differs from manifest.");
            }
            else if ((string?)pair.Value?["type"] == "directory" && !Directory.Exists(path))
                throw new InvalidDataException("Existing runtime directory is missing.");
            else if ((string?)pair.Value?["type"] is not ("directory" or "link"))
                throw new InvalidDataException("Unsupported existing runtime entry type.");
        }
    }

    private static JsonObject Raw(JsonObject descriptor) => descriptor["downloads"]?["raw"] as JsonObject
        ?? throw new InvalidDataException("Runtime file has no raw download descriptor.");
    private static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 33554432) throw new InvalidDataException("Runtime metadata exceeds its read budget.");
        return JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? throw new InvalidDataException("Runtime metadata is not an object.");
    }
    private static async Task WriteStateAsync(string root, bool complete, string manifest, string java, CancellationToken ct)
    {
        var path = Contained(root, StateFileName);
        var temp = Contained(root, StateFileName + ".tmp-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { complete, manifestSha1 = manifest, javaExecutable = java }), ct);
        File.Move(temp, path, true);
    }
    public static string PlatformKey(LauncherOS os, RuntimeArchitecture architecture) => (os, architecture) switch
    {
        (LauncherOS.MacOS, RuntimeArchitecture.Arm64) => "mac-os-arm64",
        (LauncherOS.MacOS, RuntimeArchitecture.X64) => "mac-os",
        (LauncherOS.Windows, RuntimeArchitecture.X86) => "windows-x86",
        (LauncherOS.Windows, RuntimeArchitecture.X64) => "windows-x64",
        (LauncherOS.Windows, RuntimeArchitecture.Arm64) => "windows-arm64",
        (LauncherOS.Linux, RuntimeArchitecture.X86) => "linux-i386",
        (LauncherOS.Linux, RuntimeArchitecture.X64) => "linux",
        (LauncherOS.Linux, RuntimeArchitecture.Arm64) => "linux-arm64",
        _ => throw new PlatformNotSupportedException("No Mojang runtime catalog platform mapping is defined.")
    };
    private static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value is "." or ".."
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_')) throw new ArgumentException("Invalid runtime component.");
        return value;
    }
    private static string Hash(string? value)
    {
        if (value?.Length != 40 || value.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Runtime SHA-1 is invalid.");
        return value.ToLowerInvariant();
    }
    private static Uri HttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Runtime URL must be HTTP(S) without credentials.");
        return uri;
    }
    private static int VersionMajor(string? version)
    {
        var parts = (version ?? "").Split('.', '-', '+', '_');
        return int.TryParse(parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0], out var major) ? major : 0;
    }
    private static string Contained(string root, string path, bool allowAbsolute = false)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Runtime roots must be absolute.");
        root = Canonical(root);
        if (!allowAbsolute && (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0')
            || path.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))) throw new InvalidDataException("Unsafe runtime manifest path.");
        var result = Canonical(Path.IsPathFullyQualified(path) && allowAbsolute ? path : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, result);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime path escapes the application-managed installation root.");
        return result;
    }
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unresolved runtime symbolic link.");
        }
        return Path.GetFullPath(current);
    }
    private static string LinkTarget(string root, string link, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) || target.Contains(':') || target.Contains('\0')) throw new InvalidDataException("Unsafe runtime link target.");
        return Contained(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target.Replace('/', Path.DirectorySeparatorChar))), allowAbsolute: true);
    }
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Runtime lock cannot be a symbolic link.");
    }
}
