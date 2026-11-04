namespace SHOU.Launcher.Core.IO;

public static class SafePath
{
    /// <summary>Compare normalized paths after the caller resolves links. macOS never uses Path.GetRelativePath's case folding.</summary>
    public static bool IsWithin(string root, string path)
    {
        return Java.JavaPathBoundary.ContainsCanonical(root, path);
    }
    public static string Canonical(string path)
    {
        return Java.JavaPathBoundary.Canonical(Path.GetFullPath(path));
    }
    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new IOException("Unsafe relative path.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or ".")) throw new IOException("Parent path traversal is not allowed.");
        var canonicalRoot = Canonical(root);
        var result = Canonical(Path.Combine(canonicalRoot, Path.Combine(parts)));
        if (!IsWithin(canonicalRoot, result))
            throw new IOException("Path or symbolic link escapes installation root.");
        return result;
    }
    public static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-') || value is "." or "..")
            throw new ArgumentException("Invalid identifier.", nameof(value));
        return value;
    }
}
