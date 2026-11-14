// Portable legacy analysis; host supplies logs and instance mod directories.
using System.IO;

namespace PCL;

internal sealed class CrashLogEntry(
    string fullPath,
    IReadOnlyList<string> lines, DateTime? lastWriteTime = null)
{
    public DateTime? LastWriteTime { get; } = lastWriteTime;

    public string FullPath { get; } = fullPath;

    public IReadOnlyList<string> Lines { get; } = lines;

    public string FileName => Path.GetFileName(FullPath);
}