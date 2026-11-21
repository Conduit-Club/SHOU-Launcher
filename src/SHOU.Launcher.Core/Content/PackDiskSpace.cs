namespace SHOU.Launcher.Core.Content;

internal static class PackDiskSpace
{
    internal static void Require(string directory, long bytes)
    {
        if (bytes < 0) throw new InvalidDataException("整合包文件大小无效。");
        var path = Path.GetFullPath(directory);
        while (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? Path.GetPathRoot(path)!;
        long? available = null;
        try
        {
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady && IO.SafePath.IsWithin(d.RootDirectory.FullName, path)).OrderByDescending(d => d.Name.Length).FirstOrDefault();
            available = drive?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Some network volumes cannot report space; writes still propagate actual disk errors. */ }
        if (available is >= 0 && bytes > available) throw new IOException($"磁盘可用空间不足：需要 {bytes:N0} 字节，可用 {available:N0} 字节。请选择有足够空间的目录。");
    }
}
