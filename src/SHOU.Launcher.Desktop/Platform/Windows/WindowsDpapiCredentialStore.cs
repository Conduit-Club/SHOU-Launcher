using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using PlainToolkit.CngProtectedData;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.Windows;

/// <summary>New credential records only. Legacy UserKey.bin and account files are never replaced or migrated here.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = "PCL CE Encryption Key"u8.ToArray();
    private readonly string _directory;
    private readonly string _sharedRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsDpapiCredentialStore(IAppPaths paths)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI requires Windows.");
        _sharedRoot = paths.SharedDataDirectory;
        _directory = Path.Combine(_sharedRoot, "credentials-v1");
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(Location(key), cancellationToken); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x53484f55
                || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)) != bytes.Length - 12)
                throw new CredentialStoreException(CredentialFailure.Corrupt, "凭据记录格式损坏；保留原文件。");
            var version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
            try
            {
                return version switch
                {
                    1 => ProtectedData.Unprotect(bytes[12..], Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser),
                    2 => CngProtectedData.Unprotect(bytes[12..], Entropy, PlainToolkit.CngProtectedData.DataProtectionScope.CurrentUser),
                    _ => throw new CredentialStoreException(CredentialFailure.Corrupt, "不支持的凭据保护版本；保留原文件。")
                };
            }
            catch (CryptographicException ex) { throw new CredentialStoreException(CredentialFailure.Corrupt, "DPAPI 无法解密原凭据；不会替换密钥。", ex); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机 DPAPI 暂不可用；原凭据保留。", ex); }
        }
        catch (UnauthorizedAccessException ex) { throw new CredentialStoreException(CredentialFailure.Denied, "没有读取凭据的权限。", ex); }
        catch (IOException ex) { throw new CredentialStoreException(CredentialFailure.IOFailure, "凭据读取失败；保留原文件。", ex); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        if (value.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(value));
        await _gate.WaitAsync(cancellationToken);
        byte[] raw = value.ToArray();
        string? temporary = null;
        try
        {
            var target = Location(key);
            byte[] encrypted;
            try
            {
                encrypted = CngProtectedData.Protect(raw, Entropy, PlainToolkit.CngProtectedData.DataProtectionScope.CurrentUser);
                var roundTrip = CngProtectedData.Unprotect(encrypted, Entropy, PlainToolkit.CngProtectedData.DataProtectionScope.CurrentUser);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(raw, roundTrip))
                        throw new CredentialStoreException(CredentialFailure.Unavailable, "DPAPI 写入自检失败；不会保存不可恢复的凭据。");
                }
                finally { CryptographicOperations.ZeroMemory(roundTrip); }
            }
            catch (Exception ex) when (ex is CryptographicException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { throw new CredentialStoreException(CredentialFailure.Unavailable, "本机 DPAPI 不可用；不会降级保存。", ex); }
            var bytes = new byte[12 + encrypted.Length];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x53484f55);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), encrypted.Length);
            encrypted.CopyTo(bytes, 12);
            Directory.CreateDirectory(_directory);
            temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        catch (UnauthorizedAccessException ex) { throw new CredentialStoreException(CredentialFailure.Denied, "没有保存凭据的权限。", ex); }
        catch (IOException ex) { throw new CredentialStoreException(CredentialFailure.IOFailure, "凭据未保存成功；原记录保留。", ex); }
        finally { CryptographicOperations.ZeroMemory(raw); _gate.Release(); }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { cancellationToken.ThrowIfCancellationRequested(); File.Delete(Location(key)); }
        catch (UnauthorizedAccessException ex) { throw new CredentialStoreException(CredentialFailure.Denied, "没有删除凭据的权限。", ex); }
        catch (IOException ex) { throw new CredentialStoreException(CredentialFailure.IOFailure, "凭据删除失败。", ex); }
        finally { _gate.Release(); }
    }

    private string Location(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Contains('\0')) throw new ArgumentException("Invalid credential identifier.");
        AppPaths.EnsureContained(_sharedRoot, _directory);
        var path = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".bin");
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Credential records cannot be symbolic links.");
        return path;
    }
}
