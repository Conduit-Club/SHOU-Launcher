using System.Security.Cryptography;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Authentication;

public sealed partial class AccountAppearanceStore
{
    // Private local rollback material only: public identity, model, owned cape IDs and the previous skin.
    // Never persist the authenticated response or credential/session objects.
    public async Task<string> BackupOnlineAsync(AccountProfile account, AccountAppearance appearance, CancellationToken ct = default)
    {
        if (account.Kind != AccountKind.Microsoft || appearance.AccountId != account.Id) throw new InvalidDataException("外观备份的角色不匹配。");
        var prefix = Path.GetFileNameWithoutExtension(PathFor(account.Id)) + "-online-";
        var directory = Path.Combine(root, "account-appearance");
        var parentBefore = Capture(directory, true);
        if (!parentBefore.Exists) { parentBefore.Verify(); Directory.CreateDirectory(directory); parentBefore.VerifyExistingNodes(); }
        var parent = Capture(directory);
        if (Directory.EnumerateFiles(directory, prefix + "*.json").Take(64).Count() >= 64) throw new IOException("此外观已有 64 份备份，请先整理外观备份目录。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, account.Id, account.PlayerUuid, CreatedAt = DateTimeOffset.UtcNow,
            appearance.Model, ActiveCapeId = appearance.Capes.FirstOrDefault(c => c.IsActive)?.Id,
            OwnedCapeIds = appearance.Capes.Select(c => c.Id).ToArray(), Png = appearance.Skin?.Png.ToArray(),
            Sha256 = appearance.Skin is { } skin ? Convert.ToHexString(SHA256.HashData(skin.Png.Span)) : null });
        var target = SafePath.Within(directory, prefix + Guid.NewGuid().ToString("N") + ".json");
        var stage = target + ".tmp"; WorldFileIdentity? allocated = null;
        try
        {
            parent.Verify(); var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(stage, options))
            {
                allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle); parent.Verify();
                if (!Capture(stage).Final.SameNode(allocated.Value)) throw new IOException("外观备份暂存路径已改变。");
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
                allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
            }
            ct.ThrowIfCancellationRequested(); parent.Verify();
            if (Capture(stage).Final != allocated) throw new IOException("外观备份暂存文件已改变。");
            File.Move(stage, target); parent.Verify();
            var saved = await ReadOptionalAsync(target, MaximumRecordBytes + 16384, CancellationToken.None);
            if (saved is null || !saved.AsSpan().SequenceEqual(bytes)) throw new IOException("外观备份核验失败，未发送在线修改。");
            return target;
        }
        finally
        {
            if (allocated is { } created)
                try { parent.Verify(); var remaining = Capture(stage, true); if (remaining.Exists && remaining.Final.SameNode(created)) File.Delete(stage); }
                catch (IOException) { /* Keep replaced paths intact. */ }
        }
    }
}
