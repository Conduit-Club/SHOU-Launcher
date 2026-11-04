using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SHOU.Launcher.Desktop.Platform.MacOS;

/// <summary>Obtains stored names from the filesystem; never folds names that may belong to different files.</summary>
internal static class MacFileSystemPaths
{
    public static string CanonicalDirectory(string absoluteDirectory)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteDirectory));
        var suffix = new Stack<string>();
        // A not-yet-created root uses the stored path of its nearest existing directory ancestor.
        // Preserve new names exactly, so their first creation and the next process use the same scope.
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) throw new IOException("No existing filesystem ancestor can resolve the application directory.");
            suffix.Push(Path.GetFileName(current));
            current = parent;
        }
        // sys/attr.h: fixed-arity getattrlist avoids Darwin arm64's special variadic ABI (fcntl).
        // ATTR_CMN_FULLPATH returns the actual stored path, cross-checked against native C F_GETPATH.
        var attributes = new AttributeList { BitmapCount = 5, Reserved = 0, Common = 0x08000000,
            Volume = 0, Directory = 0, File = 0, Fork = 0 };
        var buffer = new byte[4096];
        if (GetAttributes(current, ref attributes, buffer, (nuint)buffer.Length, 0) != 0) throw Failure(Marshal.GetLastPInvokeError());
        var returned = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
        var start = 4L + offset; // attrreference_t offsets are relative to the reference, not the buffer.
        if (returned < 12 || returned > buffer.Length || offset < 8 || length < 1 || start + length > returned
            || buffer[(int)(start + length - 1)] != 0)
            throw new IOException("The filesystem did not return a valid canonical path attribute.");
        var stored = new UTF8Encoding(false, true).GetString(buffer, (int)start, length - 1);
        if (!Path.IsPathFullyQualified(stored)) throw new IOException("The filesystem returned a relative stored path.");
        while (suffix.TryPop(out var part)) stored = Path.Combine(stored, part);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(stored));
    }

    private static IOException Failure(int error) => new("Cannot obtain the actual filesystem directory identity.", new Win32Exception(error));
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint Common;
        public uint Volume;
        public uint Directory;
        public uint File;
        public uint Fork;
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref AttributeList attributes, [Out] byte[] buffer, nuint bufferSize, uint options);
}
