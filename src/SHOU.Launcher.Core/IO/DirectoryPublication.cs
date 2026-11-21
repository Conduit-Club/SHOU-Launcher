using SHOU.Launcher.Core.Downloads;

namespace SHOU.Launcher.Core.IO;

public static class DirectoryPublication
{
    /// <summary>Keep publication atomic; briefly tolerate Windows handles without FILE_SHARE_DELETE.</summary>
    public static async Task PublishAsync(string staging, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            VerifiedDownloader.RejectLink(staging); VerifiedDownloader.RejectLink(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Publication destination already exists.");
            try { Directory.Move(staging, destination); return; }
            catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 5
                && ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xffff) is 5 or 32 or 33
                && Directory.Exists(staging) && !Directory.Exists(destination) && !File.Exists(destination))
            { await Task.Delay(TimeSpan.FromMilliseconds(50 << attempt), ct).ConfigureAwait(false); }
        }
    }
}
