using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SHOU.Launcher.Core.Java;

/// <summary>Filesystem canonical spelling followed by an explicit platform comparison; never infer volume identity by case folding.</summary>
public static class JavaPathBoundary
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Canonical(string path, bool followFinalLink = true)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute Java boundary path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!; var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((followFinalLink || i != parts.Length - 1) && entry.LinkTarget is not null)
            {
                var target = entry.ResolveLinkTarget(true);
                if (target is null || !target.Exists) throw new IOException("Java boundary contains an unresolved link.");
                current = target.FullName;
            }
        }
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return OperatingSystem.IsMacOS() ? StoredMacPath(normalized, followFinalLink) : normalized;
    }

    public static bool Contains(string root, string path) => ContainsCanonical(Canonical(root), Canonical(path));

    // Inputs here are already normalized/canonical. Keep this pure for boundary fixtures on every filesystem.
    public static bool ContainsCanonical(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(root, path, Comparison) || path.StartsWith(Prefix(root), Comparison);
    }

    public static string Relative(string root, string path, bool preserveFinalLink = true) =>
        RelativeCanonical(Canonical(root), Canonical(path, !preserveFinalLink));

    public static string RelativeCanonical(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!ContainsCanonical(root, path)) throw new InvalidDataException("Java path is outside its canonical root.");
        return string.Equals(root, path, Comparison) ? "." : path[Prefix(root).Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Retains ownership of literal paths below an owned root even if a descendant link escapes it.</summary>
    public static bool ContainsLiteral(string root, string path)
    {
        var canonicalRoot = Canonical(root);
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if (ContainsCanonical(canonicalRoot, Canonical(part, false))) return true;
        return false;
    }

    private static string Prefix(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

    private static string StoredMacPath(string full, bool followFinalLink)
    {
        var current = full; var suffix = new Stack<string>();
        while (!Directory.Exists(current) && !File.Exists(current) && new FileInfo(current).LinkTarget is null)
        {
            suffix.Push(Path.GetFileName(current));
            current = Path.GetDirectoryName(current) ?? throw new IOException("Java boundary has no existing filesystem ancestor.");
        }
        var attributes = new Attributes { BitmapCount = 5, Common = 0x08000000 };
        var buffer = new byte[4096];
        // The same fixed-arity ATTR_CMN_FULLPATH layout verified by the B4 native path fixtures.
        if (GetAttributes(current, ref attributes, buffer, (nuint)buffer.Length, followFinalLink ? 0u : 1u) != 0)
            throw new IOException("Cannot obtain the canonical Java filesystem path.");
        var returned = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)); var start = 4L + offset;
        if (returned < 12 || returned > buffer.Length || offset < 8 || length < 1 || start + length > returned
            || buffer[(int)(start + length - 1)] != 0) throw new IOException("Invalid canonical Java filesystem path attribute.");
        var stored = new UTF8Encoding(false, true).GetString(buffer, (int)start, length - 1);
        if (!Path.IsPathFullyQualified(stored)) throw new IOException("Canonical Java filesystem path was relative.");
        while (suffix.TryPop(out var part)) stored = Path.Combine(stored, part);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(stored));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        internal ushort BitmapCount, Reserved;
        internal uint Common, Volume, Directory, File, Fork;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref Attributes attributes, [Out] byte[] buffer, nuint length, uint options);
}
