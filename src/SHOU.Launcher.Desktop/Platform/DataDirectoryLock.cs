using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform;

public sealed class DataDirectoryLock : IDataDirectoryLock
{
    public IDataDirectoryLease Acquire(IAppPaths paths)
    {
        if (paths.IsIsolated) AppPaths.EnsureContained(paths.DataDirectory, paths.LocalDataDirectory);
        if (new FileInfo(paths.LockFile).LinkTarget is not null)
            throw new IOException("The data lock cannot be a symbolic link.");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LockFile)!);
        FileStream? stream = null;
        try
        {
            // FileShare.Read is intentionally compatible with legacy SingleInstanceService.
            stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (!OperatingSystem.IsWindows())
            {
                var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                var result = OperatingSystem.IsMacOS() ? MacFlock(fd, 2 | 4) : UnixFlock(fd, 2 | 4);
                if (result != 0)
                    throw new IOException("The application data directory is already in use.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            stream.SetLength(0);
            stream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            stream.Flush(true);
            return new Lease(paths.LockFile, stream);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private sealed class Lease(string path, FileStream stream) : IDataDirectoryLease
    {
        private FileStream? _stream = stream;
        public string LockPath { get; } = path;
        // Keep the inode. Deleting a released lock can race with the next owner's acquisition.
        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int MacFlock(int fileDescriptor, int operation);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int UnixFlock(int fileDescriptor, int operation);
}
