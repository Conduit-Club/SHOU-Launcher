namespace SHOU.Launcher.Core.IO;

public static class SafePath
{
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)?.FullName
                ?? throw new IOException("Cannot resolve symbolic link.");
        }
        return Path.GetFullPath(current);
    }
    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new IOException("Unsafe relative path.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or ".")) throw new IOException("Parent path traversal is not allowed.");
        var canonicalRoot = Canonical(root);
        var result = Canonical(Path.Combine(canonicalRoot, Path.Combine(parts)));
        var rel = Path.GetRelativePath(canonicalRoot, result);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
