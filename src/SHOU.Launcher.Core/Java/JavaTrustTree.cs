using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SHOU.Launcher.Core.Java;

/// <summary>A versioned, path-sensitive snapshot. Links are recorded, never traversed while copying.</summary>
internal static class JavaTrustTree
{
    internal sealed record Entry(string Path, string Kind, int Mode, long Size = 0,
        string? Sha256 = null, string? Target = null);
    internal sealed record Snapshot(string Sha256, IReadOnlyList<Entry> Entries);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool IsInside(string root, string path)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        return !System.IO.Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string Absolute(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute Java trust path is required.");
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    internal static string Combine(string root, string relative)
    {
        if (relative == ".") return root;
        if (string.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0')
            || relative.Split('/').Any(part => part is "." or ".." or ""))
            throw new InvalidDataException("Invalid Java trust relative path.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!IsInside(root, full)) throw new InvalidDataException("Java trust path escapes its root.");
        return full;
    }

    internal static async Task<Snapshot> ReadAsync(string root, CancellationToken ct)
    {
        root = Absolute(root);
        JavaTrustRepair.EnsureRegularPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Java trust tree does not exist.");
        var entries = new List<Entry> { new(".", "directory", Mode(root)) };
        var queue = new Queue<string>(); queue.Enqueue(root);
        long total = 0;
        while (queue.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= 100000) throw new InvalidDataException("Java trust tree exceeds its entry budget.");
                var relative = System.IO.Path.GetRelativePath(root, path).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                _ = Combine(root, relative);
                var attributes = File.GetAttributes(path);
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                var target = info.LinkTarget;
                if (target is not null)
                {
                    if (System.IO.Path.IsPathRooted(target) || target.Contains(':') || target.Contains('\\') || target.Contains('\0'))
                        throw new InvalidDataException("Java trust copy requires a contained relative link.");
                    var lexical = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, target));
                    if (!IsInside(root, lexical)) throw new InvalidDataException("Java trust link leaves JavaHome.");
                    var resolved = info.ResolveLinkTarget(true) ?? throw new InvalidDataException("Unresolved Java trust link.");
                    if (!resolved.Exists || !IsInside(root, resolved.FullName))
                        throw new InvalidDataException("Broken or external Java trust link.");
                    entries.Add(new(relative, Directory.Exists(path) ? "link-directory" : "link-file", 0, Target: target));
                }
                else if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unsupported Java trust reparse point.");
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    entries.Add(new(relative, "directory", Mode(path))); queue.Enqueue(path);
                }
                else
                {
                    EnsureSingleRegularFile(path);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length > 4L * 1024 * 1024 * 1024 || (total = checked(total + input.Length)) > 4L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Java trust tree exceeds its byte budget.");
                    var length = input.Length;
                    var digest = await DigestAsync(input, length, null, ct);
                    if (input.Length != length) throw new IOException("Java trust source changed during snapshot.");
                    entries.Add(new(relative, "file", Mode(path), length, digest));
                }
            }
        }
        var sorted = entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SHOU-JAVA-TREE\0v1\0"u8);
        foreach (var entry in sorted)
        {
            Text(entry.Path); Text(entry.Kind); Number(entry.Mode); Number(entry.Size);
            Text(entry.Sha256 ?? ""); Text(entry.Target ?? "");
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), sorted);

        void Text(string text)
        {
            var bytes = Utf8.GetBytes(text); Number(bytes.Length); hash.AppendData(bytes);
        }
        void Number(long number)
        {
            Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, number); hash.AppendData(bytes);
        }
    }

    internal static async Task CopyAsync(string source, string destination, Snapshot snapshot, CancellationToken ct)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Java trust destination already exists.");
        Directory.CreateDirectory(destination);
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == "directory" && entry.Path != "."))
            Directory.CreateDirectory(Combine(destination, entry.Path));
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == "file"))
        {
            ct.ThrowIfCancellationRequested();
            var original = Combine(source, entry.Path); var target = Combine(destination, entry.Path);
            EnsureSingleRegularFile(original);
            // CreateNew + independent writes intentionally forbid hard-link reuse of the source inode.
            await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous))
            {
                if (await DigestAsync(input, entry.Size, output, ct) != entry.Sha256)
                    throw new IOException("Java trust source changed during independent copy.");
                await output.FlushAsync(ct);
            }
            SetMode(target, entry.Mode);
            EnsureSingleRegularFile(target);
        }
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind.StartsWith("link-", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            var target = Combine(destination, entry.Path);
            if (entry.Kind == "link-directory") Directory.CreateSymbolicLink(target, entry.Target!);
            else File.CreateSymbolicLink(target, entry.Target!);
        }
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == "directory")
                     .OrderByDescending(entry => entry.Path.Length)) SetMode(Combine(destination, entry.Path), entry.Mode);
        if ((await ReadAsync(destination, ct)).Sha256 != snapshot.Sha256)
            throw new IOException("Independent Java trust copy did not preserve the source tree.");
    }

    internal static int Mode(string path) => OperatingSystem.IsWindows()
        ? ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ? 1 : 0)
        : (int)File.GetUnixFileMode(path);

    private static async Task<string> DigestAsync(Stream input, long expected, Stream? output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long count = 0;
        while (count < expected)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - count)), ct);
            if (read == 0) throw new IOException("Java trust file was truncated during read.");
            hash.AppendData(buffer.AsSpan(0, read));
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), ct);
            count += read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct) != 0) throw new IOException("Java trust file grew during read.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void SetMode(string path, int mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)mode);
        else
        {
            var attrs = File.GetAttributes(path);
            File.SetAttributes(path, mode == 1 ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly);
        }
    }

    internal static void EnsureSingleRegularFile(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var attrs = new DarwinAttributes { BitmapCount = 5, Common = 8, File = 1 };
            var buffer = new byte[12];
            if (GetAttributes(path, ref attrs, buffer, (nuint)buffer.Length, 1) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != 12
                || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)) != 1)
                throw new IOException("Java trust copy refuses special or hard-linked files.");
        }
        else if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[256];
            if (!BitConverter.IsLittleEndian || Statx(-100, path, 0x100, 5, buffer) != 0
                || (BinaryPrimitives.ReadUInt32LittleEndian(buffer) & 5) != 5
                || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)) != 1
                || (BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28)) & 0xf000) != 0x8000)
                throw new IOException("Java trust copy refuses special or hard-linked files.");
        }
        else if (OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1
                || (info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Java trust copy refuses special or hard-linked files.");
        }
        else throw new PlatformNotSupportedException("Java trust file identity is unavailable on this platform.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAttributes
    {
        internal ushort BitmapCount, Reserved;
        internal uint Common, Volume, Directory, File, Fork;
    }
    [DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref DarwinAttributes attributes, [Out] byte[] buffer, nuint size, uint options);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
}
