using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Java;

public sealed record MacJava8Package(string Url, string Sha256, long Size);

/// <summary>Native Java 8 for the macOS ARM64 gap in Mojang's legacy runtime catalog. All other requests pass through.</summary>
public sealed class MacJava8RuntimeInstaller(IJavaRuntimeInstaller fallback, IVerifiedDownloader downloader,
    IJavaDiscovery discovery, string managedRoot, string cacheRoot, MacJava8Package? package = null) : IJavaRuntimeInstaller
{
    public static MacJava8Package OfficialPackage { get; } = new(
        "https://cdn.azul.com/zulu/bin/zulu8.98.0.111-ca-jdk8.0.512-macosx_aarch64.tar.gz",
        "523a350b96563e1ff406cf3c55e0211a5816b482a67b5f88a5ea9ba799fd32be", 103741261);
    private const string StateFile = ".shou-java8.json";
    private sealed record EntryHash(string Path, string Sha256, long Size);
    private sealed record EntryLink(string Path, string Target, bool HardLink);
    private sealed record RuntimeState(string ArchiveSha256, string JavaPath, EntryHash[] Files, EntryLink[] Links);

    public async Task<JavaInstallation> InstallAsync(JavaRuntimeRequest request, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.OS != LauncherOS.MacOS || request.Architecture != RuntimeArchitecture.Arm64 || request.MajorVersion != 8 || request.Component != "jre-legacy" || request.CatalogUri is not null)
            return await fallback.InstallAsync(request, progress, cancellationToken).ConfigureAwait(false);
        var source = package ?? OfficialPackage;
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "cdn.azul.com" ||
            source.Sha256.Length != 64 || source.Sha256.Any(c => !Uri.IsHexDigit(c)) || source.Size is <= 0 or > 512L * 1024 * 1024)
            throw new InvalidDataException("Mac Java 8 官方归档身份无效。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallRoot));
        if (requested != root && !requested.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Java 安装目录超出管理范围。");
        SafePath.RejectLinks(requested); SafePath.RejectLinks(cacheRoot);
        var final = SafePath.Within(requested, "java8-macos-arm64/" + source.Sha256);
        var archive = SafePath.Within(cacheRoot, "macos-java8/" + source.Sha256 + ".tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(final)!); Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        SafePath.RejectLinks(final); SafePath.RejectLinks(archive); SafePath.RejectLinks(final + ".lock");
        await using var lease = new FileStream(final + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await downloader.DownloadAsync(new([uri], archive, Sha256: source.Sha256, Size: source.Size), progress, cancellationToken).ConfigureAwait(false);
        await using (var input = File.OpenRead(archive))
            if (input.Length != source.Size || !Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mac Java 8 归档校验失败。");
        if (Directory.Exists(final))
        {
            var statePath = Path.Combine(final, StateFile); SafePath.RejectLinks(statePath);
            var state = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(statePath, cancellationToken)) ?? throw new InvalidDataException("Mac Java 8 安装记录缺失。");
            if (state.ArchiveSha256 != source.Sha256) throw new InvalidDataException("Mac Java 8 安装记录与官方归档不符。");
            foreach (var item in state.Files)
            {
                var file = SafePath.Within(final, item.Path); SafePath.RejectLinks(file); await using var input = File.OpenRead(file);
                if (input.Length != item.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)) != item.Sha256)
                    throw new InvalidDataException("Mac Java 8 管理副本已变化，未启动游戏。");
            }
            foreach (var link in state.Links.Where(l => !l.HardLink))
            {
                var file = SafePath.Within(final, link.Path); _ = RelativeTarget(final, file, link.Target, false);
                if (new FileInfo(file).LinkTarget != link.Target && new DirectoryInfo(file).LinkTarget != link.Target) throw new InvalidDataException("Mac Java 8 链接已变化。");
            }
            return await ProbeAsync(SafePath.Within(final, state.JavaPath), cancellationToken).ConfigureAwait(false);
        }
        var staging = SafePath.Within(requested, ".staging/mac-java8-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            var files = new List<EntryHash>(); var links = new List<EntryLink>(); var names = new HashSet<string>(StringComparer.Ordinal); long total = 0;
            await using (var input = File.OpenRead(archive))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var tar = new TarReader(gzip))
            {
                while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    var name = entry.Name.Replace('\\', '/').TrimEnd('/');
                    if (name is "" or ".") continue;
                    var path = SafePath.Within(staging, name);
                    if (name == StateFile || !names.Add(name) || names.Count > 20000) throw new InvalidDataException("Java 归档条目重复或过多。");
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory: Directory.CreateDirectory(path); break;
                        case TarEntryType.RegularFile:
                        case TarEntryType.V7RegularFile:
                            if (entry.Length < 0 || (total = checked(total + entry.Length)) > 1536L * 1024 * 1024) throw new InvalidDataException("Java 归档超过解包预算。");
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                                if (entry.DataStream is { } data) await data.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode);
                            await using (var stream = File.OpenRead(path)) files.Add(new(name, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length));
                            break;
                        case TarEntryType.SymbolicLink:
                        case TarEntryType.HardLink:
                            _ = RelativeTarget(staging, path, entry.LinkName, entry.EntryType == TarEntryType.HardLink);
                            links.Add(new(name, entry.LinkName, entry.EntryType == TarEntryType.HardLink)); break;
                        default: throw new InvalidDataException("Java 归档含不支持的特殊条目。");
                    }
                }
            }
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (names.Any(n => n.StartsWith(link.Path + "/", StringComparison.Ordinal))) throw new InvalidDataException("Java 归档试图通过链接写入子文件。");
                var path = SafePath.Within(staging, link.Path); var target = RelativeTarget(staging, path, link.Target, link.HardLink); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (link.HardLink)
                {
                    SafePath.RejectLinks(target); File.Copy(target, path);
                    await using var input = File.OpenRead(path); files.Add(new(link.Path, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)), input.Length));
                }
                else if (Directory.Exists(target)) Directory.CreateSymbolicLink(path, link.Target);
                else File.CreateSymbolicLink(path, link.Target);
            }
            var java = files.Select(f => f.Path).SingleOrDefault(p => p.EndsWith("/Contents/Home/bin/java", StringComparison.Ordinal) || p == "Contents/Home/bin/java")
                ?? throw new InvalidDataException("Java 归档缺少唯一 JDK 入口。");
            _ = await ProbeAsync(SafePath.Within(staging, java), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, StateFile), JsonSerializer.Serialize(new RuntimeState(source.Sha256, java, files.ToArray(), links.ToArray())), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); Directory.Move(staging, final);
            return await ProbeAsync(SafePath.Within(final, java), cancellationToken).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task<JavaInstallation> ProbeAsync(string path, CancellationToken ct)
    {
        var result = await discovery.ProbeAsync(path, ct).ConfigureAwait(false);
        if (!result.IsUsable || result.MajorVersion != 8 || result.RuntimeArchitecture != RuntimeArchitecture.Arm64)
            throw new PlatformNotSupportedException("官方 Mac Java 8 未通过版本/ARM64 探测，未选择该运行时。");
        return result with { Source = "managed:Azul8" };
    }
    private static string RelativeTarget(string root, string link, string target, bool hardLink)
    {
        if (string.IsNullOrWhiteSpace(target) || Path.IsPathFullyQualified(target)) throw new InvalidDataException("Java 归档含绝对链接。");
        var destination = Path.GetFullPath(Path.Combine(hardLink ? root : Path.GetDirectoryName(link)!, target));
        if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Java 归档链接超出管理目录。");
        return destination;
    }
}
