using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Java;

/// <summary>Repairs independent owned copies. The source runtime is never opened for writing.</summary>
public sealed class DerivedJavaTrustService(IJavaDiscovery discovery, IProcessRunner? processes = null) : IJavaTrustService
{
    public const string RootMarkerName = ".shou-derived-java-root.json";
    public const string ProvenanceName = "provenance.json";
    private const int Schema = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record Change(string Path, JavaTrustTree.Entry? Before, JavaTrustTree.Entry? After);
    private sealed record Provenance(int Schema, string PolicyId, JavaInstallation Source,
        string? SourceManifestSha1, string SourceTreeSha256, string OutputTreeSha256,
        string JavaRelativePath, IReadOnlyList<Change> Changes);

    public Task<IReadOnlyList<JavaTrustFinding>> InspectAsync(JavaInstallation java, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var home = JavaTrustTree.Absolute(java.JavaHome);
        JavaTrustRepair.EnsureRegularPath(home);
        IReadOnlyList<JavaTrustFinding> findings = JavaTrustRepair.ScanJava(Path.Combine(home, "bin"), java.MajorVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(findings);
    }

    public async Task<JavaTrustDerivation> CreateRepairedCopyAsync(JavaTrustCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        ct.ThrowIfCancellationRequested();
        var root = JavaTrustTree.Absolute(request.DerivedRoot);
        var sourceHome = JavaTrustTree.Absolute(request.Source.JavaHome);
        JavaTrustRepair.EnsureRegularPath(root);
        JavaTrustRepair.EnsureRegularPath(sourceHome);
        root = JavaPathBoundary.Canonical(root); sourceHome = JavaPathBoundary.Canonical(sourceHome);
        if (!request.Source.IsUsable) throw new InvalidDataException("A usable source Java probe is required.");
        var literalExecutable = JavaTrustTree.Absolute(request.Source.ExecutablePath);
        if (JavaPathBoundary.ContainsLiteral(root, literalExecutable) && !JavaTrustTree.IsInside(root, sourceHome))
            throw new InvalidDataException("A derived Java executable cannot escape through an external JavaHome.");

        if (JavaTrustTree.IsInside(root, sourceHome))
        {
            var relative = JavaPathBoundary.Relative(root, sourceHome).Split('/');
            if (relative.Length != 2 || !IsKey(relative[0]) || relative[1] != "home")
                throw new InvalidDataException("Source is not a completed owned Java trust variant.");
            var variant = JavaTrustTree.Combine(root, relative[0]);
            var provenance = await ReadProvenanceAsync(variant, ct);
            if (JavaTrustTree.IsInside(root, JavaTrustTree.Absolute(provenance.Source.JavaHome)))
                throw new InvalidDataException("Recursive Java trust derivation is forbidden.");
            if (!PathEquals(request.Source.ExecutablePath, JavaTrustTree.Combine(sourceHome, provenance.JavaRelativePath)))
                throw new InvalidDataException("Derived Java executable does not match its provenance.");
            ValidateManifestRequest(request.SourceManifestSha1, provenance.SourceManifestSha1);
            await using var existingLease = Acquire(root, relative[0]);
            return await VerifyExistingAsync(root, relative[0], provenance.Source, provenance.SourceManifestSha1, ct);
        }
        if (JavaTrustTree.IsInside(sourceHome, root))
            throw new InvalidDataException("DerivedRoot cannot be inside the read-only source JavaHome.");

        var source = request.Source with { JavaHome = sourceHome, ExecutablePath = JavaPathBoundary.Canonical(request.Source.ExecutablePath, false) };
        var executableRelative = ExecutableRelative(source);
        var manifest = ValidateManifestRequest(request.SourceManifestSha1, source.ValidatedManifestSha1);
        var key = Key(source.ExecutablePath, manifest);
        Directory.CreateDirectory(root);
        await EnsureRootMarkerAsync(root, ct);
        await using var lease = Acquire(root, key);
        var final = JavaTrustTree.Combine(root, key);
        if (Directory.Exists(final)) return await VerifyExistingAsync(root, key, source, manifest, ct);
        if (File.Exists(final)) throw new IOException("Java trust variant path is occupied.");

        var before = await JavaTrustTree.ReadAsync(sourceHome, ct);
        var sourceFindings = await InspectAsync(source, ct);
        RequireInspectable(sourceFindings);
        var stagingRoot = Path.Combine(root, ".staging");
        JavaTrustRepair.EnsureRegularPath(stagingRoot); Directory.CreateDirectory(stagingRoot);
        var stage = Path.Combine(stagingRoot, key + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        await WriteStateAsync(stage, false, ct);
        var copiedHome = Path.Combine(stage, "home");
        await JavaTrustTree.CopyAsync(sourceHome, copiedHome, before, ct);
        foreach (var sourceFinding in sourceFindings.Where(finding => finding.NeedsRepair))
        {
            ct.ThrowIfCancellationRequested();
            var target = JavaTrustTree.Combine(copiedHome, Relative(sourceHome, sourceFinding.Path));
            var copiedFinding = JavaTrustRepair.Scan(target, sourceFinding.IsTrustStore);
            if (copiedFinding.Hash != sourceFinding.Hash || !copiedFinding.NeedsRepair || !copiedFinding.CanRepair)
                throw new InvalidDataException("Copied Java trust finding does not match the source.");
            var repaired = await Task.Run(() => JavaTrustRepair.Repair(copiedFinding, ct), ct);
            if (!repaired.CanRepair || repaired.NeedsRepair) throw new InvalidDataException("Java trust repair did not complete.");
        }
        var after = await JavaTrustTree.ReadAsync(copiedHome, ct);
        var changes = ValidateOutput(sourceHome, copiedHome, before, after, sourceFindings);
        await ProbeAsync(copiedHome, executableRelative, source, ct);
        if ((await JavaTrustTree.ReadAsync(copiedHome, ct)).Sha256 != after.Sha256
            || (await JavaTrustTree.ReadAsync(sourceHome, ct)).Sha256 != before.Sha256)
            throw new IOException("Java trust tree changed before publication.");
        var record = new Provenance(Schema, JavaTrustPolicy.Id, source, manifest,
            before.Sha256, after.Sha256, executableRelative, changes);
        await WriteJsonNewAsync(Path.Combine(stage, ProvenanceName), record, ct);
        // The moved directory remains undiscoverable until its final-path JVM also probes successfully.
        Directory.Move(stage, final);
        var finalHome = Path.Combine(final, "home");
        var java = await ProbeAsync(finalHome, executableRelative, source, ct);
        if ((await JavaTrustTree.ReadAsync(finalHome, ct)).Sha256 != after.Sha256
            || (await JavaTrustTree.ReadAsync(sourceHome, ct)).Sha256 != before.Sha256)
            throw new IOException("Java trust tree changed during final probe.");
        await WriteStateAsync(final, true, ct);
        return Result(java, record, Path.Combine(final, ProvenanceName), Findings(finalHome, source.MajorVersion));
    }

    private async Task<JavaTrustDerivation> VerifyExistingAsync(string root, string key, JavaInstallation source,
        string? manifest, CancellationToken ct)
    {
        var validated = await ValidateExistingAsync(root, key, source, manifest, ct);
        var final = JavaTrustTree.Combine(root, key);
        var copiedHome = Path.Combine(final, "home");
        var java = await ProbeAsync(copiedHome, validated.Record.JavaRelativePath, source, ct);
        if ((await JavaTrustTree.ReadAsync(copiedHome, ct)).Sha256 != validated.Output.Sha256
            || (await JavaTrustTree.ReadAsync(source.JavaHome, ct)).Sha256 != validated.Original.Sha256)
            throw new IOException("Java trust runtime changed during reuse probe.");
        return Result(java, validated.Record, Path.Combine(final, ProvenanceName), Findings(copiedHome, source.MajorVersion));
    }

    private static async Task<(Provenance Record, JavaTrustTree.Snapshot Original, JavaTrustTree.Snapshot Output)>
        ValidateExistingAsync(string root, string key, JavaInstallation source, string? manifest, CancellationToken ct)
    {
        var final = JavaTrustTree.Combine(root, key);
        if (!IsCompletedVariantDirectory(final)) throw new InvalidDataException("Java trust variant is incomplete.");
        var record = await ReadProvenanceAsync(final, ct);
        if (record.Schema != Schema || record.PolicyId != JavaTrustPolicy.Id || key != Key(source.ExecutablePath, manifest)
            || !PathEquals(record.Source.ExecutablePath, source.ExecutablePath)
            || !PathEquals(record.Source.JavaHome, source.JavaHome)
            || record.SourceManifestSha1 != manifest || record.Source.MajorVersion != source.MajorVersion
            || record.Source.RuntimeArchitecture != source.RuntimeArchitecture
            || record.Source.FullVersion != source.FullVersion || record.JavaRelativePath != ExecutableRelative(source)
            || JavaTrustTree.IsInside(root, source.JavaHome) || JavaTrustTree.IsInside(root, source.ExecutablePath)
            || JavaTrustTree.IsInside(source.JavaHome, root))
            throw new InvalidDataException("Java trust provenance does not match its original source.");
        var original = await JavaTrustTree.ReadAsync(source.JavaHome, ct);
        var copiedHome = Path.Combine(final, "home");
        var output = await JavaTrustTree.ReadAsync(copiedHome, ct);
        if (original.Sha256 != record.SourceTreeSha256 || output.Sha256 != record.OutputTreeSha256)
            throw new InvalidDataException("Java trust source or output was changed; no fallback is permitted.");
        var sourceFindings = Findings(source.JavaHome, source.MajorVersion); RequireInspectable(sourceFindings);
        var changes = ValidateOutput(source.JavaHome, copiedHome, original, output, sourceFindings);
        if (!JsonSerializer.SerializeToUtf8Bytes(changes).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record.Changes)))
            throw new InvalidDataException("Java trust change provenance was changed.");
        return (record, original, output);
    }

    /// <summary>Verifies a discovered candidate without executing either the source or derived Java.</summary>
    public static async Task<bool> ValidateCandidateReadOnlyAsync(string root, string executable, CancellationToken ct = default)
    {
        try
        {
            root = JavaTrustTree.Absolute(root); executable = JavaTrustTree.Absolute(executable);
            JavaTrustRepair.EnsureRegularPath(root);
            var relative = JavaPathBoundary.Relative(root, executable).Split('/');
            if (relative.Length != 4 || !IsKey(relative[0]) || relative[1] != "home"
                || relative[2] != "bin" || relative[3] != (OperatingSystem.IsWindows() ? "java.exe" : "java")) return false;
            var record = await ReadProvenanceAsync(JavaTrustTree.Combine(root, relative[0]), ct);
            await ValidateExistingAsync(root, relative[0], record.Source, record.SourceManifestSha1, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    public static string? FindDerivedRootForCandidate(string executable, string managedRoot)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(managedRoot)) return null;
        managedRoot = JavaTrustTree.Absolute(managedRoot);
        for (var directory = Path.GetDirectoryName(JavaTrustTree.Absolute(executable)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var canonical = JavaPathBoundary.Canonical(directory, false);
            if (File.Exists(Path.Combine(directory, RootMarkerName))
                || new FileInfo(Path.Combine(directory, RootMarkerName)).LinkTarget is not null
                || (Path.GetFileName(canonical) == "derived-trust" && JavaTrustTree.IsInside(managedRoot, canonical))) return canonical;
        }
        return null;
    }

    private static IReadOnlyList<Change> ValidateOutput(string sourceHome, string outputHome,
        JavaTrustTree.Snapshot source, JavaTrustTree.Snapshot output, IReadOnlyList<JavaTrustFinding> findings)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var original = source.Entries.ToDictionary(entry => entry.Path, comparer);
        var copied = output.Entries.ToDictionary(entry => entry.Path, comparer);
        var allowed = new Dictionary<string, int>(comparer);
        foreach (var finding in findings)
        {
            var relative = Relative(sourceHome, finding.Path);
            var destination = JavaTrustTree.Combine(outputHome, relative);
            var after = JavaTrustRepair.Scan(destination, finding.IsTrustStore);
            if (!after.CanRepair || after.NeedsRepair) throw new InvalidDataException("Derived runtime trust policy is incomplete.");
            if (!finding.NeedsRepair) continue;
            var existed = File.Exists(finding.Path);
            var beforeBytes = existed ? ReadTrustFile(finding.Path) : [];
            var afterBytes = ReadTrustFile(destination);
            JavaTrustRepair.ValidateTransformation(beforeBytes, afterBytes, finding.IsTrustStore);
            var backup = JavaTrustRepair.ValidateReceipt(destination, beforeBytes, afterBytes, existed);
            var privateMode = OperatingSystem.IsWindows() ? 0 : (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            allowed[relative] = original.TryGetValue(relative, out var originalEntry) ? originalEntry.Mode : privateMode;
            allowed[relative + ".shou-trust-repair.json"] = privateMode;
            allowed[Relative(outputHome, backup)] = privateMode;
        }
        var changes = new List<Change>();
        foreach (var path in original.Keys.Union(copied.Keys, comparer).Order(StringComparer.Ordinal))
        {
            original.TryGetValue(path, out var before); copied.TryGetValue(path, out var after);
            if (before == after) continue;
            if (after is null || !allowed.TryGetValue(path, out var mode) || after.Kind != "file" || after.Mode != mode)
                throw new InvalidDataException("Java trust copy changed a file outside the exact repair policy.");
            changes.Add(new(path, before, after));
        }
        foreach (var path in allowed.Keys)
            if (!copied.TryGetValue(path, out var entry) || entry.Kind != "file" || entry.Mode != allowed[path])
                throw new InvalidDataException("Java trust repair artifacts are incomplete.");
        return changes;
    }

    private async Task<JavaInstallation> ProbeAsync(string home, string relative, JavaInstallation source, CancellationToken ct)
    {
        var executable = JavaTrustTree.Combine(home, relative);
        // Production supplies its owned runner. Only explicit unit-test fixtures use the injected discovery directly.
        var java = processes is null ? await discovery.ProbeAsync(executable, ct)
            : await new JavaRuntimeProbe(processes).ProbeAsync(executable, ct);
        if (!java.IsUsable || java.MajorVersion != source.MajorVersion || java.RuntimeArchitecture != source.RuntimeArchitecture
            || java.FullVersion != source.FullVersion || !PathEquals(java.JavaHome, home) || !PathEquals(java.ExecutablePath, executable))
            throw new InvalidDataException("Derived Java failed its required relocated runtime probe.");
        return java with { Source = "managed:SHOUTrust", ValidatedManifestSha1 = null };
    }

    private static JavaTrustDerivation Result(JavaInstallation java, Provenance record, string provenance,
        IReadOnlyList<JavaTrustFinding> findings) => new(java, record.Source.ExecutablePath, JavaTrustPolicy.Id,
            record.SourceTreeSha256, record.OutputTreeSha256, provenance, findings);

    private static IReadOnlyList<JavaTrustFinding> Findings(string home, int major) =>
        JavaTrustRepair.ScanJava(Path.Combine(home, "bin"), major);
    private static void RequireInspectable(IReadOnlyList<JavaTrustFinding> findings)
    {
        if (findings.Count == 0 || findings.Any(finding => !finding.CanRepair))
            throw new InvalidDataException("Java trust inspection could not establish a repairable source policy.");
    }
    private static string ExecutableRelative(JavaInstallation source)
    {
        var relative = Relative(source.JavaHome, source.ExecutablePath);
        if (relative != "bin/" + (OperatingSystem.IsWindows() ? "java.exe" : "java"))
            throw new InvalidDataException("Source executable is not the declared JavaHome launcher.");
        return relative;
    }
    private static string Relative(string root, string path)
    {
        var relative = JavaPathBoundary.Relative(root, path);
        _ = JavaTrustTree.Combine(root, relative); return relative;
    }
    private static bool PathEquals(string left, string right) =>
        string.Equals(JavaPathBoundary.Canonical(left, false), JavaPathBoundary.Canonical(right, false), JavaTrustTree.PathComparison);
    private static string Key(string executable, string? manifest)
    {
        var normalized = JavaTrustTree.Absolute(executable);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SHOU-JAVA-TRUST-v1\0" + JavaTrustPolicy.Id + "\0" + normalized + "\0" + manifest))).ToLowerInvariant();
    }
    private static bool IsKey(string name) => name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? ValidateManifestRequest(string? requested, string? validated)
    {
        foreach (var hash in new[] { requested, validated })
            if (hash is not null && (hash.Length != 40 || !hash.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Invalid source runtime manifest SHA-1.");
        if (requested is not null && !string.Equals(requested, validated, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source manifest was not validated by its installer.");
        return (requested ?? validated)?.ToLowerInvariant();
    }

    private static FileStream Acquire(string root, string key)
    {
        var locks = Path.Combine(root, ".locks"); JavaTrustRepair.EnsureRegularPath(locks); Directory.CreateDirectory(locks);
        var path = Path.Combine(locks, key + ".lock"); JavaTrustRepair.EnsureRegularPath(path);
        if (File.Exists(path)) JavaTrustTree.EnsureSingleRegularFile(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static async Task EnsureRootMarkerAsync(string root, CancellationToken ct)
    {
        var path = Path.Combine(root, RootMarkerName); JavaTrustRepair.EnsureRegularPath(path);
        if (File.Exists(path))
        {
            using var json = JsonDocument.Parse(ReadSmallFile(path, 1024));
            if (json.RootElement.GetProperty("schema").GetInt32() != Schema) throw new InvalidDataException("Unknown Java trust root schema.");
        }
        else await WriteJsonNewAsync(path, new { schema = Schema }, ct);
    }
    public static bool IsDerivedRootDirectory(string directory) =>
        Path.GetFileName(directory).Equals("derived-trust", StringComparison.Ordinal)
        || File.Exists(Path.Combine(directory, RootMarkerName)) || new FileInfo(Path.Combine(directory, RootMarkerName)).LinkTarget is not null;

    public static bool IsCompletedVariantDirectory(string directory)
    {
        try
        {
            var state = Path.Combine(directory, MojangJavaRuntimeInstaller.StateFileName);
            var provenance = Path.Combine(directory, ProvenanceName);
            JavaTrustRepair.EnsureRegularPath(state); JavaTrustRepair.EnsureRegularPath(provenance);
            using var json = JsonDocument.Parse(ReadSmallFile(state, 65536));
            var value = json.RootElement;
            return value.GetProperty("complete").ValueKind == JsonValueKind.True
                && value.GetProperty("trustPolicy").GetString() == JavaTrustPolicy.Id
                && value.GetProperty("schema").GetInt32() == Schema
                && new FileInfo(provenance).Length is > 0 and <= 33554432;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    internal static void InvalidateForRestore(string restoredPath)
    {
        for (var directory = Path.GetDirectoryName(restoredPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (Path.GetFileName(directory) != "home") continue;
            var variant = Path.GetDirectoryName(directory)!;
            if (!File.Exists(Path.Combine(variant, ProvenanceName))) return;
            var state = Path.Combine(variant, MojangJavaRuntimeInstaller.StateFileName);
            JavaTrustRepair.EnsureRegularPath(state);
            using var json = JsonDocument.Parse(ReadSmallFile(state, 65536));
            if (!json.RootElement.TryGetProperty("trustPolicy", out var policy) || policy.GetString() != JavaTrustPolicy.Id) return;
            var temp = state + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(output, new { schema = Schema, complete = false, trustPolicy = JavaTrustPolicy.Id });
                output.Flush(true);
            }
            File.Move(temp, state, true); return;
        }
    }
    private static async Task<Provenance> ReadProvenanceAsync(string variant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(variant, ProvenanceName); JavaTrustRepair.EnsureRegularPath(path);
        var bytes = ReadSmallFile(path, 33554432);
        var record = JsonSerializer.Deserialize<Provenance>(bytes, Json) ?? throw new InvalidDataException("Invalid Java trust provenance.");
        if (record.Source is null || !record.Source.IsUsable || string.IsNullOrWhiteSpace(record.Source.JavaHome)
            || string.IsNullOrWhiteSpace(record.Source.ExecutablePath) || string.IsNullOrWhiteSpace(record.Source.FullVersion)
            || record.Changes is null || record.Changes.Count > 100000 || record.JavaRelativePath is null
            || !IsDigest(record.SourceTreeSha256) || !IsDigest(record.OutputTreeSha256))
            throw new InvalidDataException("Java trust provenance fields are incomplete.");
        if (ValidateManifestRequest(record.SourceManifestSha1, record.Source.ValidatedManifestSha1) != record.SourceManifestSha1)
            throw new InvalidDataException("Java trust source manifest provenance is inconsistent.");
        await Task.CompletedTask; return record;
    }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static byte[] ReadTrustFile(string path) => ReadSmallFile(path, 33554432);
    private static byte[] ReadSmallFile(string path, int limit)
    {
        JavaTrustRepair.EnsureRegularPath(path); JavaTrustTree.EnsureSingleRegularFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Java trust record exceeds its byte budget.");
        using var output = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, limit + 1L - output.Length))) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new InvalidDataException("Java trust record grew beyond its byte budget.");
        }
        return output.ToArray();
    }
    private static async Task WriteStateAsync(string directory, bool complete, CancellationToken ct)
    {
        var destination = Path.Combine(directory, MojangJavaRuntimeInstaller.StateFileName);
        JavaTrustRepair.EnsureRegularPath(destination);
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await WriteJsonNewAsync(temp, new { schema = Schema, complete, trustPolicy = JavaTrustPolicy.Id }, ct);
        File.Move(temp, destination, true);
    }
    private static async Task WriteJsonNewAsync<T>(string path, T value, CancellationToken ct)
    {
        JavaTrustRepair.EnsureRegularPath(path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await JsonSerializer.SerializeAsync(output, value, Json, ct); await output.FlushAsync(ct);
    }
}
