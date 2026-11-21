using SHOU.Launcher.Core.Instances;

namespace SHOU.Launcher.Core.Authentication;

public static class AccountSkinExport
{
    public static async Task SaveAsync(string path, ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        var bytes = AccountAppearancePng.Decode(png.ToArray()).Png.ToArray();
        path = Path.GetFullPath(path); var parent = Path.GetDirectoryName(path) ?? throw new IOException("请选择本机 PNG 保存位置。");
        var directory = WorldPathSnapshot.Capture(parent, parent); var original = WorldPathSnapshot.Capture(parent, path, true);
        if (original.Exists && (original.Final.Directory || File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))) throw new IOException("目标不是可写的普通文件。");
        var stage = Path.Combine(parent, ".shou-skin-" + Guid.NewGuid().ToString("N") + ".tmp"); WorldFileIdentity? allocated = null;
        try
        {
            directory.Verify();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(stage, options))
            {
                allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle); directory.Verify();
                if (!WorldPathSnapshot.Capture(parent, stage).Final.SameNode(allocated.Value)) throw new IOException("皮肤导出暂存路径已改变。");
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
                allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
            }
            ct.ThrowIfCancellationRequested(); directory.Verify(); original.Verify();
            if (WorldPathSnapshot.Capture(parent, stage).Final != allocated) throw new IOException("皮肤导出暂存文件已改变。");
            // Atomic replacement occurs only after the complete PNG has been flushed and the target rechecked.
            File.Move(stage, path, original.Exists);
        }
        finally
        {
            if (allocated is { } created)
                try { directory.Verify(); var remaining = WorldPathSnapshot.Capture(parent, stage, true); if (remaining.Exists && remaining.Final.SameNode(created)) File.Delete(stage); }
                catch (IOException) { /* Do not remove substituted paths. */ }
        }
    }
}
