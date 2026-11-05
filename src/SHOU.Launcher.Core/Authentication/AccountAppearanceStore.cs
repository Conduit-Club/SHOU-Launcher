using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Instances;
using SHOU.Launcher.Core.IO;

namespace SHOU.Launcher.Core.Authentication;

/// <summary>One atomic, account-bound local appearance record. Reuses the world store's tested path/handle identities.</summary>
public sealed class AccountAppearanceStore
{
    private const int MaximumRecordBytes = AccountAppearancePng.MaximumPngBytes * 4 / 3 + 8192;
    private readonly string root;
    private readonly WorldFileIdentity identity;
    public AccountAppearanceStore(string localDataDirectory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localDataDirectory));
        var guard = WorldPathSnapshot.Capture(root, root); identity = guard.Final;
        if (!identity.Directory) throw new IOException("外观数据根目录无效。");
    }
    public async Task<AccountAppearance> LoadOfflineAsync(AccountProfile account, CancellationToken ct = default)
    {
        RequireOffline(account); var path = PathFor(account.Id); var bytes = await ReadOptionalAsync(path, MaximumRecordBytes, ct);
        if (bytes is null) return EmptyOffline(account.Id);
        LocalRecord record;
        try { record = JsonSerializer.Deserialize<LocalRecord>(bytes, new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("本地皮肤记录损坏，原文件已保留。"); }
        if (record.Schema != 1 || record.AccountId != account.Id || !Enum.IsDefined(record.Model) || record.Png is null) throw new InvalidDataException("本地皮肤记录的账户或格式不匹配。");
        return EmptyOffline(account.Id) with { Model = record.Model, Skin = AccountAppearancePng.Decode(record.Png) };
    }
    public async Task<AccountAppearance> SaveOfflineAsync(AccountProfile account, ReadOnlyMemory<byte> png, AccountSkinModel model, CancellationToken ct = default)
    {
        RequireOffline(account); if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        var image = AccountAppearancePng.Decode(png); var path = PathFor(account.Id); var directory = Path.GetDirectoryName(path)!;
        var rootGuard = Capture(root); var directoryBefore = Capture(directory, true);
        if (!directoryBefore.Exists) { directoryBefore.Verify(); Directory.CreateDirectory(directory); directoryBefore.VerifyExistingNodes(); }
        var parent = Capture(directory); var before = Capture(path, true);
        if (before.Exists && (before.Final.Directory || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)) throw new IOException("本地皮肤记录为只读或非普通文件。");
        var original = await ReadOptionalAsync(path, MaximumRecordBytes, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LocalRecord(1, account.Id, model, image.Png.ToArray()));
        var stage = SafePath.Within(directory, ".skin-" + Guid.NewGuid().ToString("N") + ".tmp"); WorldFileIdentity? allocated = null;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            parent.Verify(); rootGuard.Verify();
            await using (var stream = new FileStream(stage, options))
            {
                allocated = WorldFileIdentity.FromHandle(stream.SafeFileHandle); parent.Verify();
                if (!Capture(stage).Final.SameNode(allocated.Value)) throw new IOException("皮肤暂存路径已改变。");
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
                var completed = WorldFileIdentity.FromHandle(stream.SafeFileHandle);
                if (!completed.SameNode(allocated.Value) || Capture(stage).Final != completed) throw new IOException("皮肤暂存文件已改变。");
                allocated = completed;
            }
            ct.ThrowIfCancellationRequested(); before.Verify(); parent.Verify(); rootGuard.Verify();
            var current = await ReadOptionalAsync(path, MaximumRecordBytes, ct);
            if (!(current is null && original is null) && (current is null || original is null || !current.AsSpan().SequenceEqual(original))) throw new IOException("本地皮肤记录已改变，请刷新后重试。");
            if (Capture(stage).Final != allocated.Value) throw new IOException("皮肤暂存文件身份已改变。");
            File.Move(stage, path, before.Exists); parent.Verify(); rootGuard.Verify();
            if (!Capture(path).Final.SameNode(allocated.Value)) throw new IOException("保存后的皮肤文件身份不匹配。");
            var saved = await ReadOptionalAsync(path, MaximumRecordBytes, ct);
            if (saved is null || !saved.AsSpan().SequenceEqual(bytes)) throw new IOException("保存后的皮肤记录校验失败。");
            return EmptyOffline(account.Id) with { Model = model, Skin = image };
        }
        finally
        {
            if (allocated is { } created)
            {
                try { parent.Verify(); var remaining = Capture(stage, true); if (remaining.Exists && remaining.Final.SameNode(created)) File.Delete(stage); }
                catch (IOException) { /* Changed paths remain untouched. */ }
            }
        }
    }
    public static async Task<byte[]> ReadPickedSkinAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path); var parent = Path.GetDirectoryName(path) ?? throw new IOException("请选择本机 PNG 文件。");
        return await ReadGuardedAsync(WorldPathSnapshot.Capture(parent, path), AccountAppearancePng.MaximumPngBytes, ct);
    }
    public static AccountAppearance EmptyOffline(string accountId) => new(accountId, AccountSkinModel.Classic, null, [], true, null,
        "仅保存此离线角色的启动器头像与预览；不会改变原版游戏身份或游戏内皮肤。");
    private static void RequireOffline(AccountProfile account)
    { if (account.Kind != AccountKind.Offline) throw new InvalidOperationException("本地皮肤只适用于离线角色；在线角色请前往对应官网修改。"); }
    private string PathFor(string id)
    {
        if (id.Length is < 1 or > 1024) throw new ArgumentException("账户标识无效。");
        return SafePath.Within(root, "account-appearance/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant() + ".json");
    }
    private WorldPathSnapshot Capture(string path, bool missing = false)
    {
        if (!WorldFileIdentity.AtPath(root).SameNode(identity)) throw new IOException("外观数据目录身份已改变。");
        return WorldPathSnapshot.Capture(root, path, missing);
    }
    private async Task<byte[]?> ReadOptionalAsync(string path, int maximum, CancellationToken ct)
    { var guard = Capture(path, true); if (!guard.Exists) return null; var result = await ReadGuardedAsync(guard, maximum, ct); Capture(root).Verify(); return result; }
    private static async Task<byte[]> ReadGuardedAsync(WorldPathSnapshot guard, int maximum, CancellationToken ct)
    {
        guard.Verify(); if (guard.Final.Directory || guard.Final.Size > maximum) throw new InvalidDataException("皮肤文件不是普通文件或超过大小上限。");
        await using var stream = new FileStream(guard.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, FileOptions.Asynchronous);
        var opened = WorldFileIdentity.FromHandle(stream.SafeFileHandle); guard.Verify();
        if (opened != guard.Final || stream.Length > maximum) throw new IOException("皮肤文件在读取前已改变。");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct);
        if (await stream.ReadAsync(new byte[1], ct) != 0 || opened != WorldFileIdentity.FromHandle(stream.SafeFileHandle)) throw new IOException("皮肤文件在读取期间已改变。");
        guard.Verify(); return bytes;
    }
    private sealed record LocalRecord(int Schema, string AccountId, AccountSkinModel Model, byte[] Png);
}
