using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Desktop.ViewModels;

public static class BundledLicenseDocuments
{
    // Keep the existing bounded, top-level document list. Runtime package catalogs stay in the bundle.
    public static IReadOnlyList<LicenseDocument> Read(string executableDirectory)
    {
        var executable = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory)));
        var roots = new List<string> { executable.FullName };
        if (executable.Name == "MacOS" && executable.Parent is { Name: "Contents" } contents && contents.Parent?.Extension == ".app")
            roots.Insert(0, Path.Combine(contents.FullName, "Resources"));
        var documents = new List<LicenseDocument>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue; SafePath.RejectLinks(root);
                var licenses = Path.Combine(root, "licenses");
                IEnumerable<FileInfo> files = [];
                if (Directory.Exists(licenses)) { SafePath.RejectLinks(licenses); files = new DirectoryInfo(licenses).EnumerateFiles().ToArray(); }
                files = files.Concat(new[] { "LICENSE", "LICENCE", "LICENSE.txt", "ThirdPartyNotices.txt", "THIRDPARTYNOTICES.txt" }.Select(n => new FileInfo(Path.Combine(root, n))).Where(f => f.Exists));
                foreach (var file in files.OrderBy(f => f.Name))
                {
                    if (documents.Count >= 100) return documents;
                    if (file.LinkTarget is not null || file.Length > 1024 * 1024 || file.Extension.ToLowerInvariant() is not (".txt" or ".md" or "") || !seen.Add(file.Name)) continue;
                    try { SafePath.RejectLinks(file.FullName); documents.Add(new(file.Name, File.ReadAllText(file.FullName))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return documents;
    }
}
